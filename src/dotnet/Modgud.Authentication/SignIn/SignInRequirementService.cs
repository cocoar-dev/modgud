using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.Realms;
using Modgud.Authentication.Identity;

namespace Modgud.Authentication.SignIn;

/// <summary>Where a sign-in decision is made: the browser login page (every factor the
/// App offers is available) or a native token grant (only what the App implements).</summary>
public enum SignInSurface
{
    Web,
    Native,
}

/// <summary>
/// ADR 0025 §3 — what a sign-in is for. <see cref="AppIds"/> are the Apps whose policy
/// applies (empty = the realm default); <see cref="Policy"/> is their combined, strictest
/// policy; <see cref="IsAdministration"/> marks the realm's admin surface.
/// </summary>
public sealed record SignInTarget(
    IReadOnlyList<Guid> AppIds,
    SignInPolicy Policy,
    string? PasskeyRpId,
    bool IsAdministration = false);

public enum SignInOutcome
{
    /// <summary>The sign-in reached what the target requires.</summary>
    Satisfied,

    /// <summary>A second factor is required and the user has one that can be used.</summary>
    NeedSecondFactor,

    /// <summary>The target requires <c>multi</c>, the user has no usable second factor, and
    /// the setup grace is over.</summary>
    SetupRequired,
}

/// <summary>
/// ADR 0025 — the answer to "does this sign-in reach what the target requires, and if
/// not, how can it". <see cref="SecondFactors"/> are usable on the evaluated surface;
/// <see cref="BrowserOnlyFactors"/> (native surface only) need the browser continuation.
/// </summary>
public sealed record SignInDecision(
    SignInOutcome Outcome,
    SignInLevel Required,
    SignInLevel Achieved,
    IReadOnlyList<string> SecondFactors,
    IReadOnlyList<string> BrowserOnlyFactors,
    bool SetupPending,
    DateTimeOffset? SetupDueAt)
{
    public bool IsSatisfied => Outcome == SignInOutcome.Satisfied;
}

public interface ISignInRequirementService
{
    /// <summary>§3: the client's single App, else the Apps of the requested resources, else
    /// the realm default. The Host is not consulted.</summary>
    Task<SignInTarget> ResolveTargetAsync(
        string? clientId, IEnumerable<string> resources, CancellationToken ct = default);

    /// <summary>The target of a sign-in on the login page, from its return URL: a pending
    /// <c>/connect/authorize</c> request names the client and resources; any other return URL
    /// is Modgud's own UI, i.e. the self-service portal (the <c>modgud</c> App).</summary>
    Task<SignInTarget> ResolveTargetFromReturnUrlAsync(string? returnUrl, CancellationToken ct = default);

    /// <summary>The realm's administration surface (<c>/api/admin/*</c>).</summary>
    Task<SignInTarget> AdministrationTargetAsync(CancellationToken ct = default);

    /// <summary>Evaluate <paramref name="factors"/> for <paramref name="user"/> against
    /// <paramref name="target"/>. With <paramref name="startSetupGrace"/> a user who owes a
    /// second factor and has none gets their grace started (persisted).</summary>
    Task<SignInDecision> EvaluateAsync(
        ApplicationUser user,
        SignInTarget target,
        IReadOnlyDictionary<string, DateTimeOffset> factors,
        SignInSurface surface,
        bool startSetupGrace,
        CancellationToken ct = default);
}

public sealed class SignInRequirementService(
    IDocumentSession session,
    IApplicationSettingsResolver settingsResolver,
    RpIdResolver rpIdResolver) : ISignInRequirementService
{
    public async Task<SignInTarget> ResolveTargetAsync(
        string? clientId, IEnumerable<string> resources, CancellationToken ct = default)
    {
        var appIds = new List<Guid>();
        string? clientRpId = null;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, ct);
            if (client is { AppIds.Count: 1 })
                appIds.Add(client.AppIds[0]);
            if (client is not null
                && client.Settings.TryGetValue(OAuthApplicationSettingKeys.WebAuthnRpId, out var rp)
                && !string.IsNullOrWhiteSpace(rp))
                clientRpId = rp;
        }

        if (appIds.Count == 0)
        {
            var names = resources.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToList();
            if (names.Count > 0)
            {
                var apis = await session.Query<OAuthApiState>()
                    .Where(a => names.Contains(a.Name) && !a.IsDeleted)
                    .ToListAsync(ct);
                appIds.AddRange(apis.Where(a => a.AppId.HasValue).Select(a => a.AppId!.Value).Distinct());
            }
        }

        return await BuildTargetAsync(appIds, clientRpId, ct);
    }

    public async Task<SignInTarget> ResolveTargetFromReturnUrlAsync(string? returnUrl, CancellationToken ct = default)
    {
        if (TryParseAuthorize(returnUrl, out var clientId, out var resources))
            return await ResolveTargetAsync(clientId, resources, ct);

        // The admin console's routes: the session is raised for the administration.
        if (returnUrl is not null
            && (returnUrl.Equals("/admin", StringComparison.OrdinalIgnoreCase)
                || returnUrl.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)
                || returnUrl.StartsWith("/admin?", StringComparison.OrdinalIgnoreCase)))
            return await AdministrationTargetAsync(ct);

        var portal = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == AppSlugs.Modgud && !a.IsDeleted, ct);
        return await BuildTargetAsync(portal is null ? [] : [portal.Id], clientRpId: null, ct);
    }

    public async Task<SignInTarget> AdministrationTargetAsync(CancellationToken ct = default)
    {
        var realm = (await settingsResolver.ResolveAsync(null, ct)).SignIn ?? SignInPolicy.Defaults;
        return new SignInTarget([], realm with { MinimumLevel = realm.AdministrationMinimumLevel }, PasskeyRpId: null,
            IsAdministration: true);
    }

    private async Task<SignInTarget> BuildTargetAsync(IReadOnlyList<Guid> appIds, string? clientRpId, CancellationToken ct)
    {
        if (appIds.Count == 0)
        {
            var realm = await settingsResolver.ResolveAsync(null, ct);
            return new SignInTarget([], realm.SignIn ?? SignInPolicy.Defaults, clientRpId);
        }

        SignInPolicy? combined = null;
        string? appRpId = null;
        foreach (var appId in appIds)
        {
            var effective = await settingsResolver.ResolveAsync(appId, ct);
            var policy = effective.SignIn ?? SignInPolicy.Defaults;
            combined = combined is null ? policy : Strictest(combined, policy);
            if (appIds.Count == 1) appRpId = effective.PasskeyRpId;
        }
        return new SignInTarget(appIds, combined!, clientRpId ?? appRpId);
    }

    /// <summary>Several Apps in one sign-in (a token for APIs of two Apps): the higher
    /// level, the shorter grace, only methods both offer, and the browser continuation
    /// when either asks for it.</summary>
    internal static SignInPolicy Strictest(SignInPolicy a, SignInPolicy b) => a with
    {
        MinimumLevel = (SignInLevel)Math.Max((int)a.MinimumLevel, (int)b.MinimumLevel),
        SetupGraceDays = Math.Min(a.SetupGraceDays, b.SetupGraceDays),
        Password = a.Password && b.Password,
        EmailCode = a.EmailCode && b.EmailCode,
        Passkey = a.Passkey && b.Passkey,
        Totp = a.Totp && b.Totp,
        EmailAfterPassword = a.EmailAfterPassword && b.EmailAfterPassword,
        OwnFactorNotOffered = (OwnFactorNotOffered)Math.Max((int)a.OwnFactorNotOffered, (int)b.OwnFactorNotOffered),
    };

    public async Task<SignInDecision> EvaluateAsync(
        ApplicationUser user,
        SignInTarget target,
        IReadOnlyDictionary<string, DateTimeOffset> factors,
        SignInSurface surface,
        bool startSetupGrace,
        CancellationToken ct = default)
    {
        var passkeyRpIds = (await session.Query<StoredPasskeyCredential>()
            .Where(c => c.UserId == user.Id)
            .Select(c => c.RpId)
            .ToListAsync(ct)).ToList();
        // A passkey only counts where it can be used: bound to the target's RP ID (the
        // client's or App's own, else the realm's). A legacy credential without an RP ID
        // belongs to the realm's primary domain.
        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        var targetRpId = target.PasskeyRpId ?? primaryDomain;
        var hasUsablePasskey = passkeyRpIds.Any(rp => string.Equals(rp ?? primaryDomain, targetRpId, StringComparison.OrdinalIgnoreCase));

        var security = await session.LoadAsync<UserSecurityData>(user.Id, ct);
        var hasAnySecondFactor = user.TwoFactorEnabled
            || (user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email))
            || passkeyRpIds.Count > 0;

        var facts = new SignInFacts(
            HasTotp: user.TwoFactorEnabled,
            HasEmailSecondFactor: user.EmailOtpEnabled && !string.IsNullOrEmpty(user.Email),
            HasUsablePasskey: hasUsablePasskey,
            HasAnySecondFactor: hasAnySecondFactor,
            Exempt: security?.TwoFactorExempt == true,
            SetupDueAt: security?.SecureSetupDueAt is { } due ? new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Utc)) : null);

        var decision = Evaluate(target.Policy, facts, factors.Keys.ToHashSet(StringComparer.Ordinal), surface, DateTimeOffset.UtcNow);

        // Start the setup grace at the first sign-in that owes a second factor the user
        // does not have. Persisted once; later sign-ins read the stamped date.
        if (startSetupGrace && decision.SetupPending && facts.SetupDueAt is null && !facts.Exempt)
        {
            security ??= UserSecurityData.Create(user.Id);
            var graceDays = Math.Max(0, security.GracePeriodDaysOverride ?? target.Policy.SetupGraceDays);
            security.SecureSetupDueAt = DateTime.UtcNow.AddDays(graceDays);
            session.Store(security);
            await session.SaveChangesAsync(ct);
            decision = Evaluate(target.Policy, facts with { SetupDueAt = new DateTimeOffset(security.SecureSetupDueAt.Value, TimeSpan.Zero) },
                factors.Keys.ToHashSet(StringComparer.Ordinal), surface, DateTimeOffset.UtcNow);
        }

        return decision;
    }

    /// <summary>What the evaluation needs to know about the user.</summary>
    internal sealed record SignInFacts(
        bool HasTotp,
        bool HasEmailSecondFactor,
        bool HasUsablePasskey,
        bool HasAnySecondFactor,
        bool Exempt,
        DateTimeOffset? SetupDueAt);

    /// <summary>
    /// ADR 0025 §7 — the required level is the higher of the App's minimum and the second
    /// factor the user switched on themselves (honoured where the App offers it, or through
    /// the browser when the App asks for that). A stored passkey never raises it.
    /// </summary>
    internal static SignInDecision Evaluate(
        SignInPolicy policy, SignInFacts user, IReadOnlySet<string> proven, SignInSurface surface, DateTimeOffset now)
    {
        var achieved = SignInAssurance.LevelOf(proven);
        var viaBrowser = policy.OwnFactorNotOffered == OwnFactorNotOffered.RequireViaBrowser;
        var passwordOnly = proven.Contains(SignInMethods.Password) && !proven.Contains(SignInMethods.Email);

        // Second factors this user can use here, given what is already proven.
        var webTotp = user.HasTotp && !proven.Contains(SignInMethods.Totp) && (policy.Totp || viaBrowser);
        var webEmail = user.HasEmailSecondFactor && passwordOnly && (policy.EmailAfterPassword || viaBrowser);
        var webPasskey = user.HasUsablePasskey && policy.Passkey && !proven.Contains(SignInMethods.Passkey);

        // The user's own second factor: a demand wherever it can be honoured.
        var ownDemand = (user.HasTotp && (policy.Totp || viaBrowser))
            || (user.HasEmailSecondFactor && passwordOnly && (policy.EmailAfterPassword || viaBrowser));

        var required = ownDemand && policy.MinimumLevel < SignInLevel.Multi ? SignInLevel.Multi : policy.MinimumLevel;
        if (achieved >= required)
            return new SignInDecision(SignInOutcome.Satisfied, required, achieved, [], [], false, null);

        var usable = new List<string>();
        var browserOnly = new List<string>();
        if (surface == SignInSurface.Web)
        {
            if (webTotp) usable.Add(SignInMethods.Totp);
            if (webEmail) usable.Add(SignInMethods.Email);
            if (webPasskey) usable.Add(SignInMethods.Passkey);
        }
        else
        {
            // Natively the App can only ask for what it implements; the rest needs the browser.
            if (webTotp) (policy.Totp ? usable : browserOnly).Add(SignInMethods.Totp);
            if (webEmail) (policy.EmailAfterPassword ? usable : browserOnly).Add(SignInMethods.Email);
            // A passkey bound to the App's RP ID is used through the native passkey grant.
            if (webPasskey) usable.Add(SignInMethods.Passkey);
        }

        if (usable.Count > 0 || browserOnly.Count > 0)
            return new SignInDecision(SignInOutcome.NeedSecondFactor, required, achieved, usable, browserOnly, false, null);

        // Required by the App, and the user has nothing to reach it with: the setup duty.
        if (user.Exempt)
            return new SignInDecision(SignInOutcome.Satisfied, required, achieved, [], [], false, null);
        if (user.SetupDueAt is null || user.SetupDueAt > now)
            return new SignInDecision(SignInOutcome.Satisfied, required, achieved, [], [], SetupPending: true, user.SetupDueAt);
        return new SignInDecision(SignInOutcome.SetupRequired, required, achieved, [], [], SetupPending: true, user.SetupDueAt);
    }

    /// <summary>Parse a local <c>/connect/authorize</c> continuation: its <c>client_id</c> and
    /// every <c>resource</c>. Anything else is not an authorize continuation.</summary>
    public static bool TryParseAuthorize(string? returnUrl, out string? clientId, out IReadOnlyList<string> resources)
    {
        clientId = null;
        resources = [];
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
            return false;
        var queryStart = returnUrl.IndexOf('?');
        var path = queryStart < 0 ? returnUrl : returnUrl[..queryStart];
        if (!string.Equals(path, "/connect/authorize", StringComparison.OrdinalIgnoreCase) || queryStart < 0)
            return false;
        var query = QueryHelpers.ParseQuery(returnUrl[queryStart..]);
        if (query.TryGetValue("client_id", out var ids) && ids.Count == 1) clientId = ids[0];
        resources = query.TryGetValue("resource", out var r) ? r.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList() : [];
        return true;
    }
}
