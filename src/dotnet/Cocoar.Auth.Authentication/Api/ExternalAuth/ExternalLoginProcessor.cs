using System.Security.Claims;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Domain.Users.Events;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;


namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

/// <summary>
/// Orchestrates the post-callback half of an OIDC login. Decides whether the
/// external identity maps to an existing Cocoar.Auth user (by link or, carefully,
/// by email), creates a user JIT when allowed, runs the IdP's user-update
/// script to apply property patches (Firstname / Lastname / Email / Acronym),
/// and emits the events that keep PrincipalDirectory and the link aggregates
/// in sync.
/// <para>
/// Returns a <see cref="ExternalLoginResult"/> with the resolved user id and
/// the claims principal to sign in. The caller (finish endpoint) issues
/// <c>SignInAsync(ApplicationScheme, principal)</c> and clears the External
/// cookie; keeping I/O here out of the processor leaves it unit-testable.
/// </para>
/// </summary>
public class ExternalLoginProcessor(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    UserUpdateScriptRunner scriptRunner,
    ILogger<ExternalLoginProcessor> logger,
    TimeProvider clock)
{
    public async Task<ExternalLoginResult> ProcessAsync(
        ClaimsPrincipal externalPrincipal,
        Guid loginProviderId,
        CancellationToken ct,
        Guid? authenticatedUserId = null)
    {
        var config = await session.LoadAsync<LoginProvider>(loginProviderId, ct);
        if (config is null || config.IsDeleted || !config.Enabled)
            return ExternalLoginResult.Failed("Idp.NotEnabled", "This identity provider is not available.");

        // Type-discriminator gate. The callback flow is OIDC-shaped end-to-end —
        // a non-Oidc provider id reaching this point is either a stale query
        // string from before the provider was retyped, or a misconfigured
        // /start with a manually-edited id. Surface the same error code the
        // admin/runtime paths use so the frontend can render a single message.
        if (config.Type != LoginProviderType.Oidc)
        {
            var err = LoginProviderErrors.TypeNotSupported(config.Type);
            logger.LogWarning(
                "Auth: External login rejected — LoginProvider {Id} has type {Type}, expected Oidc",
                loginProviderId, config.Type);
            return ExternalLoginResult.Failed(err.Code, err.Description);
        }

        var issuer = externalPrincipal.FindFirst("iss")?.Value
            ?? externalPrincipal.Claims.FirstOrDefault()?.Issuer
            ?? string.Empty;
        var subject = externalPrincipal.FindFirst("sub")?.Value
            ?? externalPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Auth: External login missing iss/sub (config {Id})", loginProviderId);
            return ExternalLoginResult.Failed("Idp.InvalidToken", "The identity provider did not return a subject.");
        }

        var rawClaims = ExtractRawClaims(externalPrincipal);
        var scriptResult = scriptRunner.Run(config.UserUpdateScript, rawClaims);
        if (!scriptResult.Succeeded)
        {
            logger.LogWarning(
                "Auth: UserUpdateScript failed for LoginProvider {Id} subject {Sub} — {Error}; continuing without property updates",
                loginProviderId, subject, scriptResult.Error);
        }

        var capturedAt = clock.GetUtcNow();

        // 1. Existing link → happy path
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Issuer == issuer && l.Subject == subject)
            .FirstOrDefaultAsync(ct);

        if (link is not null)
        {
            // Cross-user hijack guard: if the caller is already authenticated as
            // a different user, refuse regardless of link state.
            if (authenticatedUserId is { } authId && authId != link.UserId)
            {
                logger.LogWarning(
                    "Auth: Link-attempt rejected — external subject already linked to different user (authUser={AuthId}, linkUser={LinkId})",
                    authId, link.UserId);
                return ExternalLoginResult.Failed("Idp.LinkedToOtherUser",
                    "This identity is already linked to another Cocoar.Auth account.");
            }

            var user = await userManager.FindByIdAsync(link.UserId.ToString());

            if (user is null)
            {
                // Stale link — the referred user no longer exists. Whether it
                // was soft-unlinked (legacy code path) or just orphaned, the
                // right action is the same: hard-delete + archive the stream
                // so the (Issuer, Subject) slot frees up for the fresh
                // matched/JIT user below. Soft-unlink is not enough — the
                // unique index on (Issuer, Subject) is not partial on
                // IsUnlinked, so a tombstone still blocks a new link.
                logger.LogWarning(
                    "Auth: Stale link {LinkId} → missing user {UserId}; hard-deleting and falling through",
                    link.Id, link.UserId);
                session.Delete<ExternalIdentityLink>(link.Id);
                session.Events.ArchiveStream(link.Id);
                await session.SaveChangesAsync(ct);
                link = null;  // drop through to the no-link branches below
            }
            else if (link.IsUnlinked)
            {
                // User still exists but has explicitly unlinked this IdP from
                // their profile. Don't silently re-link — require them to go
                // through Profile → Security again.
                return ExternalLoginResult.Failed("Idp.Unlinked", "This external identity has been disconnected.");
            }
            else
            {
                // Apply user-update-script patches. Email-conflict is a hard reject.
                var applyResult = await ApplyUserUpdatesAsync(user, scriptResult, ct);
                if (applyResult is not null)
                    return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);

                await RecordScriptRunAsync(link, config, scriptResult, rawClaims, capturedAt, ct);
                logger.LogInformation("Auth: External login (returning) user {UserId} via IdP {IdpId}", user.Id, loginProviderId);
                return Success(user, link, externalPrincipal, loginProviderId, issuer);
            }
        }

        // ── Linking to the currently-authenticated user ──────────────
        // Caller already has an app session and just added the external
        // identity on purpose — create the link, no JIT, no auto-link-by-email.
        if (authenticatedUserId is { } linkForUserId)
        {
            var existing = await userManager.FindByIdAsync(linkForUserId.ToString());
            if (existing is null)
                return ExternalLoginResult.Failed("Idp.UserMissing", "Your account could not be loaded.");

            var applyResult = await ApplyUserUpdatesAsync(existing, scriptResult, ct);
            if (applyResult is not null)
                return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);

            var addedLink = await CreateLinkAsync(
                existing.Id, loginProviderId, issuer, subject, scriptResult, rawClaims,
                config.StoreRawClaims, capturedAt, ct);
            logger.LogInformation(
                "Auth: External identity linked to existing user {UserId} via IdP {IdpId}",
                existing.Id, loginProviderId);
            return Success(existing, addedLink, externalPrincipal, loginProviderId, issuer);
        }

        // 2. No link — domain allowlist gate (use email from script output)
        var email = scriptResult.Email.Presence == FieldPresence.Value ? scriptResult.Email.Value : null;
        if (!IsEmailAllowed(config, email))
        {
            logger.LogWarning(
                "Auth: External login rejected — email '{MaskedEmail}' not in allowlist for IdP {IdpId}",
                LogPiiMasking.MaskEmail(email), loginProviderId);
            return ExternalLoginResult.Failed("Idp.EmailNotAllowed", "Your email domain is not allowed for this provider.");
        }

        // 3. Trust-for-email-link: auto-link to existing user with same email
        if (config.TrustForEmailLink && !string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.ToUpperInvariant();
            var existing = await session.Query<Person>()
                .Where(p => !p.IsDeleted && p.NormalizedEmail == normalizedEmail)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                var user = await userManager.FindByIdAsync(existing.Id.ToString());
                if (user is not null)
                {
                    var applyResult = await ApplyUserUpdatesAsync(user, scriptResult, ct);
                    if (applyResult is not null)
                        return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);

                    var newLink = await CreateLinkAsync(user.Id, loginProviderId, issuer, subject, scriptResult, rawClaims, config.StoreRawClaims, capturedAt, ct);
                    logger.LogInformation(
                        "Auth: External login (email-linked) user {UserId} via IdP {IdpId}", user.Id, loginProviderId);
                    return Success(user, newLink, externalPrincipal, loginProviderId, issuer);
                }
            }
        }

        // 4. JIT user creation
        if (!config.AutoCreateUsers)
        {
            logger.LogWarning(
                "Auth: External login rejected — no existing link, AutoCreateUsers=false for IdP {IdpId}", loginProviderId);
            return ExternalLoginResult.Failed("Idp.NoUserAndAutoCreateOff",
                "No user is linked to this identity and automatic creation is disabled.");
        }

        if (string.IsNullOrWhiteSpace(email))
            return ExternalLoginResult.Failed("Idp.EmailRequired",
                "Cannot create a new user without an email claim from the identity provider.");

        // Email-uniqueness on JIT: another user already owns this email → reject.
        var emailUpper = email.ToUpperInvariant();
        var emailTaken = await session.Query<Person>()
            .Where(p => !p.IsDeleted && p.NormalizedEmail == emailUpper)
            .AnyAsync(ct);
        if (emailTaken)
        {
            logger.LogWarning(
                "Auth: JIT creation rejected — email '{MaskedEmail}' is already taken by another user (IdP {IdpId})",
                LogPiiMasking.MaskEmail(email), loginProviderId);
            return ExternalLoginResult.Failed("Idp.EmailConflict",
                "A Cocoar.Auth account with this email already exists. Please contact your administrator.");
        }

        var created = await CreateUserJitAsync(email, scriptResult, ct);
        if (created is null)
            return ExternalLoginResult.Failed("Idp.JitCreationFailed", "Could not create a new user account.");

        var jitLink = await CreateLinkAsync(created.Id, loginProviderId, issuer, subject, scriptResult, rawClaims, config.StoreRawClaims, capturedAt, ct);
        logger.LogInformation("Auth: External login (JIT-created) user {UserId} via IdP {IdpId}", created.Id, loginProviderId);
        return Success(created, jitLink, externalPrincipal, loginProviderId, issuer);
    }

    /// <summary>
    /// Applies the script-emitted property patches to an existing user. Email
    /// conflicts (another user owns the target email) are hard errors — the
    /// login is rejected rather than silently skipping the email update.
    /// </summary>
    private async Task<ApplyUpdatesError?> ApplyUserUpdatesAsync(
        ApplicationUser user,
        UserUpdateResult script,
        CancellationToken ct)
    {
        if (!script.Succeeded) return null;  // script failed → no patches to apply

        Optional<string> firstnamePatch = default;
        Optional<string> lastnamePatch = default;
        Optional<string> acronymPatch = default;
        Optional<string> emailPatch = default;

        // Email requires uniqueness check.
        if (script.Email.IsSet)
        {
            var newEmail = script.Email.Value; // null means "clear"
            var newNormalized = newEmail?.ToUpperInvariant();

            // Only enforce uniqueness when the email actually changes.
            if (!string.Equals(user.NormalizedEmail, newNormalized, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(newEmail))
                {
                    var clashingUserId = await session.Query<Person>()
                        .Where(p => !p.IsDeleted && p.NormalizedEmail == newNormalized && p.Id != user.Id)
                        .Select(p => p.Id)
                        .FirstOrDefaultAsync(ct);
                    if (clashingUserId != Guid.Empty)
                    {
                        logger.LogWarning(
                            "Auth: UserUpdateScript email conflict — '{MaskedEmail}' is already taken by user {OtherId}; login rejected for user {UserId}",
                            LogPiiMasking.MaskEmail(newEmail), clashingUserId, user.Id);
                        return new ApplyUpdatesError(
                            "Idp.EmailConflict",
                            "The identity provider reports an email that is already used by another Cocoar.Auth account.");
                    }
                }

                user.Email = newEmail;
                user.NormalizedEmail = newNormalized;
                emailPatch = (Optional<string>)newEmail!;
            }
        }

        if (script.Firstname.IsSet && !string.Equals(user.Firstname, script.Firstname.Value, StringComparison.Ordinal))
        {
            user.Firstname = script.Firstname.Value;
            firstnamePatch = (Optional<string>)script.Firstname.Value!;
        }
        if (script.Lastname.IsSet && !string.Equals(user.Lastname, script.Lastname.Value, StringComparison.Ordinal))
        {
            user.Lastname = script.Lastname.Value;
            lastnamePatch = (Optional<string>)script.Lastname.Value!;
        }
        if (script.Acronym.IsSet && !string.Equals(user.Acronym, script.Acronym.Value, StringComparison.Ordinal))
        {
            user.Acronym = script.Acronym.Value;
            acronymPatch = (Optional<string>)script.Acronym.Value!;
        }

        var hasChanges = firstnamePatch.HasValue || lastnamePatch.HasValue
            || acronymPatch.HasValue || emailPatch.HasValue;

        if (hasChanges)
        {
            // Update the ApplicationUser (Identity store) so Login/Hash checks
            // see the fresh state, AND emit UserUpdatedEvent so the projections
            // (UserView, PrincipalDirectory, label-sync handlers) stay in sync.
            var result = await userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                logger.LogError("Auth: UserUpdateScript property update failed — {Errors}",
                    string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
                return new ApplyUpdatesError(
                    "Idp.UserUpdateFailed",
                    "Failed to update the user record with claims from the identity provider.");
            }

            session.Events.Append(user.Id, new UserUpdatedEvent(
                user.Id,
                Firstname: firstnamePatch,
                Lastname: lastnamePatch,
                Acronym: acronymPatch,
                Email: emailPatch));
            // SaveChanges is done by the caller (RecordScriptRunAsync / CreateLinkAsync).
        }

        return null;
    }

    private ExternalLoginResult Success(
        ApplicationUser user,
        ExternalIdentityLink link,
        ClaimsPrincipal external,
        Guid loginProviderId,
        string issuer)
    {
        // Build the sign-in ClaimsPrincipal. Post-refactor we carry only the
        // minimum needed for session mechanics — link id + issuer for logout
        // routing, amr for TwoFactorFederated. Groups/roles/email etc. are
        // **not** on the session: persistent membership is the sole source of
        // truth (see the IdP-authentication-only design note).
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        if (!string.IsNullOrWhiteSpace(user.UserName))
            identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
        identity.AddClaim(new Claim("timetodo.external.issuer", issuer));
        identity.AddClaim(new Claim("timetodo.external.linkId", link.Id.ToString()));
        identity.AddClaim(new Claim("cocoar.external.loginProviderId", loginProviderId.ToString()));

        // Preserve AMR from the external ticket for TwoFactorFederated detection.
        foreach (var amr in external.FindAll("amr"))
            identity.AddClaim(new Claim("timetodo.external.amr", amr.Value));

        return new ExternalLoginResult(
            Succeeded: true,
            UserId: user.Id,
            LinkId: link.Id,
            Principal: new ClaimsPrincipal(identity),
            ErrorCode: null,
            ErrorMessage: null);
    }

    private static bool IsEmailAllowed(LoginProvider config, string? email)
    {
        if (config.AllowedEmailDomains is null || config.AllowedEmailDomains.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return false;
        var domain = email[(email.IndexOf('@') + 1)..];
        return config.AllowedEmailDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ExternalIdentityLink> CreateLinkAsync(
        Guid userId,
        Guid loginProviderId,
        string issuer,
        string subject,
        UserUpdateResult script,
        IReadOnlyDictionary<string, object?> rawClaims,
        bool storeRawClaims,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        var linkId = Guid.NewGuid();
        var email = script.Email.Presence == FieldPresence.Value ? script.Email.Value : null;
        var displayName = BuildDisplayName(script);

        var linkedEvent = new ExternalIdentityLinkedEvent(
            Id: linkId,
            UserId: userId,
            LoginProviderId: loginProviderId,
            Issuer: issuer,
            Subject: subject,
            Email: email,
            DisplayName: displayName,
            LinkedAt: capturedAt);

        session.Events.StartStream<ExternalIdentityLink>(linkId, linkedEvent);

        // Snapshot the initial script output immediately.
        session.Events.Append(linkId, new ExternalIdentityScriptRecordedEvent(
            Id: linkId,
            CapturedAt: capturedAt,
            ScriptSucceeded: script.Succeeded,
            ScriptOutput: script.ScriptOutput,
            ScriptError: script.Error,
            RawClaims: storeRawClaims ? SerializeRawClaims(rawClaims) : null,
            Email: email,
            DisplayName: displayName));

        // Mirror onto the user's stream so PrincipalDirectory picks up the ref.
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            UserId: userId,
            LinkId: linkId,
            LoginProviderId: loginProviderId,
            Issuer: issuer,
            LinkedAt: capturedAt));

        session.Events.Append(userId, new UserLoggedInEvent(userId, IpAddress: null));

        await session.SaveChangesAsync(ct);

        // Reload the materialized link so the caller gets back the projected form.
        return (await session.LoadAsync<ExternalIdentityLink>(linkId, ct))!;
    }

    private async Task RecordScriptRunAsync(
        ExternalIdentityLink link,
        LoginProvider config,
        UserUpdateResult script,
        IReadOnlyDictionary<string, object?> rawClaims,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        var email = script.Email.Presence == FieldPresence.Value ? script.Email.Value : null;
        var displayName = BuildDisplayName(script);

        session.Events.Append(link.Id, new ExternalIdentityScriptRecordedEvent(
            Id: link.Id,
            CapturedAt: capturedAt,
            ScriptSucceeded: script.Succeeded,
            ScriptOutput: script.ScriptOutput,
            ScriptError: script.Error,
            RawClaims: config.StoreRawClaims ? SerializeRawClaims(rawClaims) : null,
            Email: email,
            DisplayName: displayName));

        session.Events.Append(link.UserId, new UserLoggedInEvent(link.UserId, IpAddress: null));

        await session.SaveChangesAsync(ct);
    }

    private async Task<ApplicationUser?> CreateUserJitAsync(string email, UserUpdateResult script, CancellationToken ct)
    {
        // Generate a username from email-local-part, disambiguate if taken.
        var localPart = email[..email.IndexOf('@')];
        var baseUserName = new string(localPart.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_').ToArray());
        if (string.IsNullOrWhiteSpace(baseUserName)) baseUserName = "user";
        var candidateUserName = baseUserName;
        var suffix = 1;
        while (await session.Query<Person>()
            .Where(p => p.AccountName == candidateUserName && !p.IsDeleted)
            .AnyAsync(ct))
        {
            suffix++;
            candidateUserName = $"{baseUserName}{suffix}";
            if (suffix > 1000) return null; // Safeguard against runaway
        }

        var user = new ApplicationUser(candidateUserName, email)
        {
            Id = Guid.NewGuid(),
            Firstname = script.Firstname.Presence == FieldPresence.Value ? script.Firstname.Value ?? string.Empty : string.Empty,
            Lastname = script.Lastname.Presence == FieldPresence.Value ? script.Lastname.Value ?? string.Empty : string.Empty,
            Acronym = script.Acronym.Presence == FieldPresence.Value ? script.Acronym.Value ?? string.Empty : string.Empty,
            IsActive = true,
        };
        var result = await userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            logger.LogError("Auth: JIT user creation failed — {Errors}",
                string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
            return null;
        }
        return user;
    }

    private static string? BuildDisplayName(UserUpdateResult script)
    {
        var fn = script.Firstname.Presence == FieldPresence.Value ? script.Firstname.Value : null;
        var ln = script.Lastname.Presence == FieldPresence.Value ? script.Lastname.Value : null;
        var combined = $"{fn} {ln}".Trim();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static JsonDocument SerializeRawClaims(IReadOnlyDictionary<string, object?> raw)
    {
        var json = JsonSerializer.Serialize(raw);
        return JsonDocument.Parse(json);
    }

    private static IReadOnlyDictionary<string, object?> ExtractRawClaims(ClaimsPrincipal principal)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in principal.Claims)
        {
            if (!dict.TryGetValue(c.Type, out var list))
            {
                list = [];
                dict[c.Type] = list;
            }
            list.Add(c.Value);
        }
        // Collapse single-value claims to scalar for ergonomic access in scripts:
        // claims.email is a string, claims.groups is an array.
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in dict)
            result[k] = v.Count == 1 ? v[0] : v.ToArray();
        return result;
    }

    private record ApplyUpdatesError(string ErrorCode, string ErrorMessage);
}

public record ExternalLoginResult(
    bool Succeeded,
    Guid? UserId,
    Guid? LinkId,
    ClaimsPrincipal? Principal,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ExternalLoginResult Failed(string code, string message) =>
        new(false, null, null, null, code, message);
}
