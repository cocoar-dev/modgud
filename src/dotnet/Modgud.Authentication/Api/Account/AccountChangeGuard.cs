using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.SignIn;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Api.Account;

/// <summary>What a user changed about their account's protection.</summary>
public enum AccountChange
{
    TotpAdded,
    TotpRemoved,
    EmailSecondFactorAdded,
    EmailSecondFactorRemoved,
    PasskeyAdded,
    PasskeyRemoved,
    PasswordChanged,
    EmailChangeRequested,
    DeletionRequested,
}

/// <summary>The result of checking an account change: allowed, or which proof is missing.</summary>
public sealed record AccountChangeCheck(bool Allowed, bool HasAccountFactor, IReadOnlyList<string> Methods);

/// <summary>
/// ADR 0025 amendment C — changing what protects an account needs a recent proof of the
/// user's account factor, or, for a user without one, of what they have. "Recent" is read
/// from the session, which records when each factor was proven: a sign-in a minute ago is
/// the proof. Every allowed change sends a notice to the account's e-mail address.
/// </summary>
public interface IAccountChangeGuard
{
    Task<AccountChangeCheck> CheckAsync(HttpContext http, ApplicationUser user, CancellationToken ct = default);

    /// <summary>Null when the change may go ahead; otherwise the 403 to return.</summary>
    Task<IResult?> RequireRecentProofAsync(HttpContext http, ApplicationUser user, CancellationToken ct = default);

    /// <summary>Mail the account-change notice. Best effort: a failed mail never fails the change.</summary>
    Task NotifyAsync(ApplicationUser user, AccountChange change, CancellationToken ct = default);
}

public sealed class AccountChangeGuard(
    IDocumentSession session,
    RpIdResolver rpIdResolver,
    IEmailService email,
    IEmailBrandingResolver branding,
    ILogger<AccountChangeGuard> logger) : IAccountChangeGuard
{
    /// <summary>How long a proven factor counts as recent for an account change.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Session factors that prove an account factor: TOTP, a passkey (one used on a
    /// Modgud page is one Modgud can check there), a federated sign-in that asserted MFA.</summary>
    private static readonly HashSet<string> AccountFactorProofs = new(StringComparer.Ordinal)
    {
        SignInMethods.Totp, SignInMethods.Passkey, SignInMethods.ExternalMfa,
    };

    public async Task<AccountChangeCheck> CheckAsync(HttpContext http, ApplicationUser user, CancellationToken ct = default)
    {
        var accountFactors = await AccountFactorsAsync(http, user, ct);
        var proven = SignInAssurance.ReadFactors(http.User);
        var since = DateTimeOffset.UtcNow - Window;

        if (accountFactors.Count > 0)
        {
            var allowed = proven.Any(f => AccountFactorProofs.Contains(f.Key) && f.Value >= since);
            return new AccountChangeCheck(allowed, HasAccountFactor: true, accountFactors);
        }

        var methods = new List<string>();
        if (!string.IsNullOrEmpty(user.Email)) methods.Add(SignInMethods.Email);
        if (!string.IsNullOrEmpty(user.PasswordHash)) methods.Add(SignInMethods.Password);
        return new AccountChangeCheck(proven.Values.Any(at => at >= since), HasAccountFactor: false, methods);
    }

    public async Task<IResult?> RequireRecentProofAsync(HttpContext http, ApplicationUser user, CancellationToken ct = default)
    {
        var check = await CheckAsync(http, user, ct);
        if (check.Allowed) return null;
        return Results.Json(new
        {
            Message = check.HasAccountFactor
                ? "Confirm this change with your second factor."
                : "Confirm this change by signing in again.",
            RequiresReauthentication = true,
            check.Methods,
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The account factors a user has (amendment B): TOTP, and a passkey for an RP ID the
    /// current page is served under — the realm's own (a legacy credential without an RP ID
    /// belongs to the realm's primary domain), or an App's when the page runs on that App's
    /// domain. A passkey bound to another App's RP ID is a way to sign in to that App and
    /// does not protect the account.
    /// </summary>
    private async Task<List<string>> AccountFactorsAsync(HttpContext http, ApplicationUser user, CancellationToken ct)
    {
        var factors = new List<string>();
        if (user.TwoFactorEnabled) factors.Add(SignInMethods.Totp);

        var rpIds = await session.Query<StoredPasskeyCredential>()
            .Where(c => c.UserId == user.Id)
            .Select(c => c.RpId)
            .ToListAsync(ct);
        if (rpIds.Count > 0)
        {
            var primary = await rpIdResolver.GetPrimaryDomainAsync(ct);
            var host = http.Request.Host.Host;
            if (rpIds.Any(rp => SignInRequirementService.IsHostUnderRpId(host, rp ?? primary)))
                factors.Add(SignInMethods.Passkey);
        }
        return factors;
    }

    public async Task NotifyAsync(ApplicationUser user, AccountChange change, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(user.Email)) return;
        try
        {
            var model = await branding.ApplyAsync(new Dictionary<string, string>
            {
                ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
            }, ct: ct);
            var english = string.Equals(model.GetValueOrDefault("Language"), "en", StringComparison.OrdinalIgnoreCase);
            model["Change"] = Describe(change, english);
            await email.SendTemplatedEmailAsync(user.Email, EmailTemplate.AccountChanged, model, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Account-change notice could not be sent to user {UserId} ({Change})", user.Id, change);
        }
    }

    private static string Describe(AccountChange change, bool english) => (change, english) switch
    {
        (AccountChange.TotpAdded, true) => "An authenticator app was set up.",
        (AccountChange.TotpAdded, false) => "Eine Authenticator-App wurde eingerichtet.",
        (AccountChange.TotpRemoved, true) => "The authenticator app was removed.",
        (AccountChange.TotpRemoved, false) => "Die Authenticator-App wurde entfernt.",
        (AccountChange.EmailSecondFactorAdded, true) => "The e-mail code was switched on as a second factor.",
        (AccountChange.EmailSecondFactorAdded, false) => "Der E-Mail-Code wurde als zweiter Faktor eingeschaltet.",
        (AccountChange.EmailSecondFactorRemoved, true) => "The e-mail code was switched off as a second factor.",
        (AccountChange.EmailSecondFactorRemoved, false) => "Der E-Mail-Code wurde als zweiter Faktor ausgeschaltet.",
        (AccountChange.PasskeyAdded, true) => "A passkey was added.",
        (AccountChange.PasskeyAdded, false) => "Ein Passkey wurde hinzugefügt.",
        (AccountChange.PasskeyRemoved, true) => "A passkey was removed.",
        (AccountChange.PasskeyRemoved, false) => "Ein Passkey wurde entfernt.",
        (AccountChange.PasswordChanged, true) => "The password was changed.",
        (AccountChange.PasswordChanged, false) => "Das Passwort wurde geändert.",
        (AccountChange.EmailChangeRequested, true) => "A change of the e-mail address was requested.",
        (AccountChange.EmailChangeRequested, false) => "Eine Änderung der E-Mail-Adresse wurde beantragt.",
        (AccountChange.DeletionRequested, true) => "The deletion of the account was requested.",
        (AccountChange.DeletionRequested, false) => "Die Löschung des Kontos wurde beantragt.",
        _ => change.ToString(),
    };
}
