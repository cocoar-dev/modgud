using JasperFx.Events.Projections;
using Marten;
using Modgud.Authentication.AuthLog;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Domain.Common;
using Modgud.Domain.Users.Events;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;
using Modgud.Authentication.Domain.Saml;
using Modgud.Authentication.Identity.ExternalAuth;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Projections;

namespace Modgud.Authentication.Setup;

/// <summary>
/// Marten wiring for the authentication slice. Hardcoded for Modgud — when adopting
/// this slice into another app, copy + adjust the document configs and event aliases here.
/// </summary>
public static class MartenStoreOptionsExtensions
{
    /// <summary>
    /// Wires the authentication schema into a Marten <see cref="StoreOptions"/>:
    /// <list type="bullet">
    ///   <item>Document schemas for Identity documents (ApplicationUser, UserSecurityData, etc.).</item>
    ///   <item>Stable event-type aliases for all identity and external-auth events.</item>
    ///   <item>Inline projections for IdpConfig and ExternalIdentityLink (login-flow reads).</item>
    /// </list>
    /// <para>
    /// Must be called AFTER <c>UseSystemTextJsonForSerialization</c> is configured on the store —
    /// call this from within the Marten options lambda, after
    /// <c>options.ConfigureDocumentStore()</c> (which sets up STJ).
    /// </para>
    /// </summary>
    public static StoreOptions UseModgudAuthentication(this StoreOptions options)
    {
        // Identity documents
        options.Schema.For<ApplicationUser>()
            .Identity(x => x.Id)
            .UniqueIndex(x => x.NormalizedUserName);
        // The NormalizedEmail index is a partial UNIQUE index
        // (WHERE NOT is_deleted) owned during the transition by
        // EmailUniquenessMigration — built out-of-band so it can refuse on
        // active duplicates rather than crash boot. Once every realm is clean
        // and the migration is removed, declare it here as a partial unique
        // computed index. See the Account-Lifecycle plan (WS2).

        options.Schema.For<UserSecurityData>()
            .Identity(x => x.Id);

        options.Schema.For<EmailOtpChallenge>()
            .Identity(x => x.Id);

        options.Schema.For<MagicLinkChallenge>()
            .Identity(x => x.Id)
            .Index(x => x.UserId);

        // C15 — bootstrap-invite for first-admin creation in a realm.
        // Stored per-tenant (one realm = one tenant DB) and indexed by
        // TokenHash for the hot lookup the bootstrap endpoint runs.
        options.Schema.For<PendingAdminInvite>()
            .Identity(x => x.Id)
            .Index(x => x.TokenHash)
            .Index(x => x.Email);

        options.Schema.For<UserChangeRequest>()
            .Identity(x => x.Id)
            .Index(x => x.UserId)
            .Index(x => x.Status);

        // Per-user device sessions (regular Marten document — not event-sourced).
        // Indexed by UserId for the "list my sessions" view and the admin
        // force-logout flow.
        options.Schema.For<UserSession>()
            .Identity(x => x.Id)
            .Index(x => x.UserId)
            .Index(x => x.ExpiresAt);

        // GDPR: per-user deletion bookkeeping (pending request + masked flag).
        // Keyed on the user id so we can simply Load it.
        options.Schema.For<UserDeletionState>()
            .Identity(x => x.Id);

        // Federation v1: per-user claims-per-source snapshot (refreshable, NOT
        // event-sourced). Keyed on the user id like UserDeletionState so it can
        // be Loaded/Deleted directly. Scrubbed by a plain Delete on user delete
        // / GDPR erase — there is no stream to mask, so NO event-masking rule.
        options.Schema.For<ExternalClaimsStore>()
            .Identity(x => x.Id);

        options.Schema.For<LoginProvider>()
            .Identity(x => x.Id)
            .Index(x => x.Type)
            .Index(x => x.Flavor)
            .Index(x => x.Enabled)
            .Index(x => x.IsDeleted);

        options.Schema.For<ExternalIdentityLink>()
            .Identity(x => x.Id)
            .UniqueIndex(x => x.Issuer, x => x.Subject)
            .Index(x => x.UserId)
            .Index(x => x.LoginProviderId)
            .Index(x => x.IsUnlinked);

        options.Schema.For<AuthLogDocument>()
            .DatabaseSchemaName("marten")
            .Identity(x => x.Id)
            .Index(x => x.Timestamp);

        // Tenant-scoped singleton config doc. One row per tenant DB,
        // addressed by the fixed `RealmSettings.SingletonId`. Owned by
        // the realm-admin via /api/admin/realm-settings.
        options.Schema.For<RealmSettingsDoc>()
            .Identity(x => x.Id);

        // Tenant-scoped singleton — per-realm SAML SP certificate state
        // (PFX bytes DataProtection-encrypted, active + previous slots for
        // rotation overlap). Lazily generated by SamlSpCertificateService.
        options.Schema.For<SamlSpCertificateDocument>()
            .Identity(x => x.Id);

        // Identity events
        options.Events.MapEventType<UserIdentitySetupEvent>("user_identity_setup");
        options.Events.MapEventType<UserUserNameChangedEvent>("user_username_changed");
        options.Events.MapEventType<UserPasswordChangedEvent>("user_password_changed");
        options.Events.MapEventType<UserLoggedInEvent>("user_logged_in");
        options.Events.MapEventType<UserLoginFailedEvent>("user_login_failed");
        options.Events.MapEventType<UserLockedOutEvent>("user_locked_out");
        options.Events.MapEventType<UserUnlockedEvent>("user_unlocked");
        options.Events.MapEventType<UserActivatedEvent>("user_activated");
        options.Events.MapEventType<UserDeactivatedEvent>("user_deactivated");

        // Login provider events (event-aliases keep the legacy `idp_config_*`
        // wire format because there is no migration concern — the names are an
        // implementation detail of stored streams. New deployments only see
        // these aliases via the event store; class names are LoginProvider*.)
        options.Events.MapEventType<LoginProviderAddedEvent>("login_provider_added");
        options.Events.MapEventType<LoginProviderUpdatedEvent>("login_provider_updated");
        options.Events.MapEventType<LoginProviderSecretRotatedEvent>("login_provider_secret_rotated");
        options.Events.MapEventType<LoginProviderEnabledEvent>("login_provider_enabled");
        options.Events.MapEventType<LoginProviderDisabledEvent>("login_provider_disabled");
        options.Events.MapEventType<LoginProviderDeletedEvent>("login_provider_deleted");

        // External identity link events
        options.Events.MapEventType<ExternalIdentityLinkedEvent>("external_identity_linked");
        options.Events.MapEventType<ExternalIdentityScriptRecordedEvent>("external_identity_script_recorded");
        options.Events.MapEventType<ExternalIdentityUnlinkedEvent>("external_identity_unlinked");

        // User-stream mirror events for external auth
        options.Events.MapEventType<UserExternalIdentityLinkedEvent>("user_external_identity_linked");
        options.Events.MapEventType<UserExternalIdentityUnlinkedEvent>("user_external_identity_unlinked");

        // ── GDPR data-masking rules ──────────────────────────────────────
        // When ApplyEventDataMasking is invoked for a user stream, these
        // rules rewrite each PII-bearing event to replace personal data
        // with placeholder values. The user id stays intact so projections
        // can still associate the masked record with the (now-archived)
        // user. Masked-out events keep the same shape so deserialization
        // doesn't break for older daemons.

        options.Events.AddMaskingRuleForProtectedInformation<UserCreatedEvent>(e =>
            new UserCreatedEvent(
                e.Id,
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]")));

        options.Events.AddMaskingRuleForProtectedInformation<UserUpdatedEvent>(e =>
            new UserUpdatedEvent(
                e.Id,
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]"),
                new Optional<string>("[DELETED]")));

        options.Events.AddMaskingRuleForProtectedInformation<UserUserNameChangedEvent>(e =>
            new UserUserNameChangedEvent(e.UserId, "[DELETED]"));

        options.Events.AddMaskingRuleForProtectedInformation<UserProfileUpdatedEvent>(e =>
            new UserProfileUpdatedEvent(e.UserId, "[DELETED]", "[DELETED]", "[DELETED]"));

        options.Events.AddMaskingRuleForProtectedInformation<UserIdentitySetupEvent>(e =>
            new UserIdentitySetupEvent(e.UserId, "[DELETED]", e.IsActive));

        // IP addresses are PII under GDPR — strip them from login records.
        options.Events.AddMaskingRuleForProtectedInformation<UserLoggedInEvent>(e =>
            new UserLoggedInEvent(e.UserId, IpAddress: null));

        options.Events.AddMaskingRuleForProtectedInformation<UserLoginFailedEvent>(e =>
            new UserLoginFailedEvent(e.UserId, IpAddress: null));

        // External identity-link streams carry the same class of PII on their
        // OWN streams: Email/DisplayName/Subject on the link event, and the raw
        // IdP claim payload + script output (which can echo any upstream claim)
        // on the per-login script-recorded event. A GDPR erase masks these
        // alongside the user stream (see GdprService.PerformPermanentEraseAsync).
        options.Events.AddMaskingRuleForProtectedInformation<ExternalIdentityLinkedEvent>(e =>
            e with { Subject = "[DELETED]", Email = "[DELETED]", DisplayName = "[DELETED]" });

        options.Events.AddMaskingRuleForProtectedInformation<ExternalIdentityScriptRecordedEvent>(e =>
            e with { ScriptOutput = null, ScriptError = null, RawClaims = null, Email = "[DELETED]", DisplayName = "[DELETED]" });

        // External-auth projections — inline (login flow reads these synchronously)
        options.Projections.Add<LoginProviderProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<ExternalIdentityLinkProjection>(ProjectionLifecycle.Inline);

        // Unified principal projection — Person+Group doc, inline for synchronously-consistent
        // permission checks + auto-membership eval.
        options.Projections.Add<ModgudPrincipalProjection>(ProjectionLifecycle.Inline);

        // View projections — composite async. Single stage now that the IdP-only
        // surface only has UserView; kept as a composite so adopters can append
        // their own app-specific views as additional stages without rewriting wiring.
        options.Projections.CompositeProjectionFor("ViewProjections", projection =>
        {
            projection.Add<UserViewProjection>();
            projection.Add<Modgud.Infrastructure.Persistence.Marten.Projections.Inbox.InboxItemProjection>();
        });

        return options;
    }
}
