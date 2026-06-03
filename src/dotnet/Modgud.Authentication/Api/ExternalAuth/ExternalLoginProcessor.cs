using System.Security.Claims;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Domain.Common;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.Users.Events;
using Modgud.Authentication.Identity.ExternalAuth;
using Modgud.Permissions.Abstractions;


namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Orchestrates the post-callback half of an OIDC login. Decides whether the
/// external identity maps to an existing Modgud user (by link or, carefully,
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
    ILoginTimeMembershipDeriver membershipDeriver,
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

        // Type-discriminator gate. Oidc + Saml are both supported here — both
        // produce a ClaimsPrincipal with iss/sub claims plus arbitrary extra
        // claims that the user-update script can pull from. Internal / Ldap /
        // Kerberos types reaching this point are misconfigured callers
        // (stale id, wrong endpoint) and get the same error code the admin
        // and runtime paths use so the frontend renders one message.
        if (config.Type != LoginProviderType.Oidc && config.Type != LoginProviderType.Saml)
        {
            var err = LoginProviderErrors.TypeNotSupported(config.Type);
            logger.LogWarning(
                "Auth: External login rejected — LoginProvider {Id} has type {Type}, expected Oidc or Saml",
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

        // Federation v1: the current provider's groups claim drives the in-memory
        // session membership derivation in Success() (always an array post-capture).
        var externalGroups = ClaimValues(rawClaims, "groups");

        // 1. Existing link → happy path (or "forget + rematch" for a dead link)
        var link = await session.Query<ExternalIdentityLink>()
            .Where(l => l.Issuer == issuer && l.Subject == subject)
            .FirstOrDefaultAsync(ct);

        if (link is not null)
        {
            var linkedUser = await userManager.FindByIdAsync(link.UserId.ToString());

            // Variant C — "unlink forgets the binding". A link is dead when its
            // user no longer exists (stale/orphaned) OR it carries a legacy
            // IsUnlinked tombstone (pre-ShouldDelete data). A dead link must never
            // block a fresh login: append the terminal ExternalIdentityUnlinkedEvent
            // so the inline projection's ShouldDelete drops the doc — freeing the
            // (Issuer, Subject) slot — then fall through to the no-link matching
            // chain (authed-link / email-trust / JIT) which re-binds by policy.
            // (Driving the delete via the event rather than session.Delete keeps the
            // stream live + maskable for a later GDPR erase and is rebuild-safe.)
            // The match key is (Issuer, Subject), not the old link id — so the
            // identity can re-home to a different user once it has been released.
            if (linkedUser is null || link.IsUnlinked)
            {
                logger.LogInformation(
                    "Auth: External identity {State} link {LinkId} forgotten (was user {UserId}) — re-matching by policy",
                    link.IsUnlinked ? "unlinked" : "stale", link.Id, link.UserId);
                session.Events.Append(link.Id,
                    new ExternalIdentityUnlinkedEvent(link.Id, capturedAt, link.UserId));
                await session.SaveChangesAsync(ct);
                link = null;  // drop through to the no-link branches below
            }
            else
            {
                // Live link. Cross-user hijack guard: a live link cannot be
                // stolen by a different authenticated user. (A forgotten /
                // unlinked (iss,sub) is fair game for re-homing above — but a
                // live one is not.)
                if (authenticatedUserId is { } authId && authId != link.UserId)
                {
                    logger.LogWarning(
                        "Auth: Link-attempt rejected — external subject already linked to different user (authUser={AuthId}, linkUser={LinkId})",
                        authId, link.UserId);
                    return ExternalLoginResult.Failed("Idp.LinkedToOtherUser",
                        "This identity is already linked to another Modgud account.");
                }

                // Apply user-update-script patches only when this provider is
                // authoritative for the profile (decision A) — the existing link's
                // IsCreator covers the JIT-creator default so a JIT-created user's
                // profile isn't frozen. Email-conflict is a hard reject.
                if (ShouldPatchProfile(config, link))
                {
                    var applyResult = await ApplyUserUpdatesAsync(linkedUser, scriptResult, ct);
                    if (applyResult is not null)
                        return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);
                }

                await RecordScriptRunAsync(link, config, scriptResult, rawClaims, capturedAt, ct);
                logger.LogInformation("Auth: External login (returning) user {UserId} via IdP {IdpId}", linkedUser.Id, loginProviderId);
                return await Success(linkedUser, link, externalPrincipal, loginProviderId, issuer, config, externalGroups, ct);
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

            // New link to an existing account — this provider did not create the
            // user, so it patches the profile only if explicitly authoritative.
            if (ShouldPatchProfile(config, link: null))
            {
                var applyResult = await ApplyUserUpdatesAsync(existing, scriptResult, ct);
                if (applyResult is not null)
                    return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);
            }

            var addedLink = await CreateLinkAsync(
                existing.Id, loginProviderId, issuer, subject, scriptResult, rawClaims,
                config.StoreRawClaims, config.Slug, isCreator: false, capturedAt, ct);
            logger.LogInformation(
                "Auth: External identity linked to existing user {UserId} via IdP {IdpId}",
                existing.Id, loginProviderId);
            return await Success(existing, addedLink, externalPrincipal, loginProviderId, issuer, config, externalGroups, ct);
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
                    // New email-matched link to an existing account — not the
                    // creator, so patch the profile only if explicitly authoritative.
                    if (ShouldPatchProfile(config, link: null))
                    {
                        var applyResult = await ApplyUserUpdatesAsync(user, scriptResult, ct);
                        if (applyResult is not null)
                            return ExternalLoginResult.Failed(applyResult.ErrorCode, applyResult.ErrorMessage);
                    }

                    var newLink = await CreateLinkAsync(user.Id, loginProviderId, issuer, subject, scriptResult, rawClaims, config.StoreRawClaims, config.Slug, isCreator: false, capturedAt, ct);
                    logger.LogInformation(
                        "Auth: External login (email-linked) user {UserId} via IdP {IdpId}", user.Id, loginProviderId);
                    return await Success(user, newLink, externalPrincipal, loginProviderId, issuer, config, externalGroups, ct);
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
                "A Modgud account with this email already exists. Please contact your administrator.");
        }

        var created = await CreateUserJitAsync(email, scriptResult, ct);
        if (created is null)
            return ExternalLoginResult.Failed("Idp.JitCreationFailed", "Could not create a new user account.");

        // This provider created the user — mark the link as the creator so it
        // stays profile-authoritative by default (decision A).
        var jitLink = await CreateLinkAsync(created.Id, loginProviderId, issuer, subject, scriptResult, rawClaims, config.StoreRawClaims, config.Slug, isCreator: true, capturedAt, ct);
        logger.LogInformation("Auth: External login (JIT-created) user {UserId} via IdP {IdpId}", created.Id, loginProviderId);
        return await Success(created, jitLink, externalPrincipal, loginProviderId, issuer, config, externalGroups, ct);
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
                            "The identity provider reports an email that is already used by another Modgud account.");
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

    private async Task<ExternalLoginResult> Success(
        ApplicationUser user,
        ExternalIdentityLink link,
        ClaimsPrincipal external,
        Guid loginProviderId,
        string issuer,
        LoginProvider config,
        IReadOnlyList<string> externalGroups,
        CancellationToken ct)
    {
        // A deactivated or deleted user must never receive a fresh app cookie via
        // federation. Password / magic-link / passkey login all gate this, and the
        // admin recycle-bin relies on IsActive as its ONLY lockout — so a binned
        // user holding an external IdP link could otherwise re-authenticate through
        // that provider and bypass the bin. RevokeAllAccessAsync only kills EXISTING
        // sessions/tokens; it does not stop a fresh login. JIT-created users are
        // IsActive=true, so the JIT path passes this gate unaffected.
        if (user.IsDeleted || !user.IsActive)
        {
            logger.LogWarning(
                "Auth: External login rejected — user {UserId} is inactive or deleted", user.Id);
            return ExternalLoginResult.Failed("Idp.UserInactive", "This account is not active.");
        }

        // Build the sign-in ClaimsPrincipal. It carries session mechanics — link
        // id + issuer for logout routing, amr for TwoFactorFederated — PLUS, for a
        // provider trusted for authorization, the federation v1 "session group"
        // claims: one INTERNAL no-destination claim per ExternallyDrivable group
        // this login matched. That claim is copied into the OpenIddict grant and
        // unioned into resource_access at token time, but is NEVER emitted to the
        // wire (the hub boundary). The session is the lease (decision D/E). Durable
        // roles/permissions still resolve from persistent membership at token time.
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        if (!string.IsNullOrWhiteSpace(user.UserName))
            identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
        identity.AddClaim(new Claim("modgud.external.issuer", issuer));
        identity.AddClaim(new Claim("modgud.external.linkId", link.Id.ToString()));
        identity.AddClaim(new Claim("modgud.external.loginProviderId", loginProviderId.ToString()));

        // Preserve AMR from the external ticket for TwoFactorFederated detection.
        foreach (var amr in external.FindAll("amr"))
            identity.AddClaim(new Claim("modgud.external.amr", amr.Value));

        // Federation v1: derive ExternallyDrivable group membership in-memory from
        // (local ∪ this provider's claims), gated on the per-provider
        // TrustForAuthorization opt-in. A password / untrusted-provider login
        // carries none. realm:admin is never externally derivable (guarded both at
        // config-write time and defensively inside the deriver).
        if (config.TrustForAuthorization)
        {
            var derived = await membershipDeriver.DeriveAsync(
                user.Id, externalGroups, $"provider:{config.Slug}", ct);
            foreach (var groupId in derived.MatchedGroupIds)
                identity.AddClaim(new Claim(FederationClaimTypes.SessionGroup, groupId.ToString()));
            if (derived.MatchedGroupIds.Count > 0)
                logger.LogInformation(
                    "Auth: external-derived grant — user {UserId} via IdP {IdpId} ({Slug}) matched {Count} session group(s)",
                    user.Id, loginProviderId, config.Slug, derived.MatchedGroupIds.Count);
        }

        return new ExternalLoginResult(
            Succeeded: true,
            UserId: user.Id,
            LinkId: link.Id,
            Principal: new ClaimsPrincipal(identity),
            ErrorCode: null,
            ErrorMessage: null);
    }

    /// <summary>
    /// Federation v1 (decision A): does THIS provider write the four profile
    /// fields on this login? True if it is explicitly authoritative, or — for the
    /// returning-link path — if its link is the JIT creator's (the default
    /// authority until an admin promotes another provider). New links (link-to-
    /// authed / email-match) are not creators, so they patch only when explicitly
    /// authoritative. Replaces the old every-provider-patches-every-login flapping.
    /// </summary>
    private static bool ShouldPatchProfile(LoginProvider config, ExternalIdentityLink? link)
        => config.AuthoritativeForProfile || link is { IsCreator: true };

    /// <summary>Reads a claim's values as a string array (scalar → one element, absent → empty).</summary>
    private static string[] ClaimValues(IReadOnlyDictionary<string, object?> rawClaims, string type)
        => rawClaims.TryGetValue(type, out var v)
            ? v switch { string[] arr => arr, string s => [s], _ => [] }
            : [];

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
        string providerSlug,
        bool isCreator,
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
            LinkedAt: capturedAt,
            IsCreator: isCreator);

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

        session.Events.Append(userId, new UserLoggedInEvent(userId, IpAddress: null,
            Method: Modgud.Infrastructure.Observability.ModgudMeters.LoginMethod.External));

        // Federation v1: refresh this provider's claims snapshot in the same
        // transaction as the link write.
        await StageClaimsStoreRefreshAsync(userId, providerSlug, rawClaims, capturedAt, ct);

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

        session.Events.Append(link.UserId, new UserLoggedInEvent(link.UserId, IpAddress: null,
            Method: Modgud.Infrastructure.Observability.ModgudMeters.LoginMethod.External));

        // Federation v1: refresh this provider's claims snapshot in the same
        // transaction as the login write.
        await StageClaimsStoreRefreshAsync(link.UserId, config.Slug, rawClaims, capturedAt, ct);

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

    /// <summary>
    /// Federation v1 — refreshes the current provider's slice of the per-user
    /// <see cref="ExternalClaimsStore"/> (decision B): delete every entry tagged
    /// <c>provider:&lt;slug&gt;</c>, then write the freshly-captured claims. Local
    /// and other-provider entries are left untouched (SET/FORCE reconcile, one
    /// provider only). Stages onto the session WITHOUT saving — the caller's
    /// single <c>SaveChangesAsync</c> commits it atomically with the login write.
    /// </summary>
    private async Task StageClaimsStoreRefreshAsync(
        Guid userId,
        string providerSlug,
        IReadOnlyDictionary<string, object?> rawClaims,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        var source = $"provider:{providerSlug}";
        var store = await session.LoadAsync<ExternalClaimsStore>(userId, ct)
                    ?? new ExternalClaimsStore { Id = userId };

        store.Claims.RemoveAll(e => e.Source == source);
        foreach (var (type, value) in rawClaims)
        {
            switch (value)
            {
                case string s:
                    store.Claims.Add(new ClaimEntry(source, type, s, capturedAt));
                    break;
                case string[] arr:
                    foreach (var v in arr)
                        store.Claims.Add(new ClaimEntry(source, type, v, capturedAt));
                    break;
            }
        }

        session.Store(store);
    }

    // Claim types that are semantically multi-valued and MUST stay arrays even
    // when the IdP emits exactly one value — otherwise a single-group/role user
    // collapses to a scalar string and a script doing `claims.groups.includes(...)`
    // breaks (string.includes is substring-match). Federation v1, decision F/I15.
    private static readonly HashSet<string> AlwaysArrayClaims =
        new(StringComparer.OrdinalIgnoreCase) { "groups", "roles", "amr" };

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
        // Collapse single-value claims to scalar for ergonomic access in scripts
        // (claims.email is a string), EXCEPT known multi-valued claims which stay
        // arrays regardless of count so membership scripts can always use array
        // semantics (claims.groups is always an array).
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in dict)
            result[k] = v.Count == 1 && !AlwaysArrayClaims.Contains(k) ? v[0] : v.ToArray();
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
