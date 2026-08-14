using JasperFx.Events.Projections;
using Marten;
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
using Modgud.Authentication.SelfRegistration.Domain;

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
            .UniqueIndex(x => x.NormalizedUserName)
            // Partial UNIQUE index on NormalizedEmail — the email-uniqueness
            // invariant, DB-enforced. Partial (WHERE NOT is_deleted) so the
            // email is reserved across the active + both pending-deletion states
            // and released only at permanent erase (where it is nulled and
            // IsDeleted flips true). Declared declaratively now that there are no
            // legacy instances with pre-index duplicate/PII data to retrofit —
            // the temporary EmailUniquenessMigration that previously owned this
            // index (out-of-band, so it could refuse on active duplicates rather
            // than crash boot) has been removed.
            .Index(x => x.NormalizedEmail, idx =>
            {
                idx.IsUnique = true;
                idx.Predicate =
                    "(data ->> 'NormalizedEmail') IS NOT NULL " +
                    "AND COALESCE((data ->> 'IsDeleted')::boolean, false) = false";
            });

        options.Schema.For<UserSecurityData>()
            .Identity(x => x.Id);

        // Same one-time-use reasoning as MagicLinkChallenge below: the consume is
        // a version-checked Store of ConsumedAt (Marten does not version-check
        // deletes), so exactly one of two concurrent redemptions of the same code
        // wins and the loser gets a ConcurrencyException. Re-issuing a code for a
        // user mutates the loaded row rather than storing a fresh instance, so the
        // version chain stays intact.
        options.Schema.For<EmailOtpChallenge>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true);

        // Audit #25 — optimistic concurrency makes the "one-time use" consume
        // atomic. Login loads the challenge then deletes it; two concurrent
        // redemptions of the same link both pass the null/expiry check before
        // either deletes. The version-checked delete lets exactly one win — the
        // loser's stale delete throws a ConcurrencyException, which the login
        // endpoint maps to a 401 "already used".
        options.Schema.For<MagicLinkChallenge>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.UserId);

        // C15 — bootstrap-invite for first-admin creation in a realm.
        // Stored per-tenant (one realm = one tenant DB) and indexed by
        // TokenHash for the hot lookup the bootstrap endpoint runs.
        options.Schema.For<PendingAdminInvite>()
            .Identity(x => x.Id)
            .Index(x => x.TokenHash)
            .Index(x => x.Email);

        // ADR-0012 — single-use registration invite codes. Indexed by CodeHash
        // (the hot redeem lookup) and AppId (per-app list/prune). Optimistic
        // concurrency makes the single-use consume atomic the same way it does
        // for MagicLinkChallenge above: two concurrent redemptions of one bearer
        // code both pass the unused/expiry check, but the version-checked update
        // lets exactly one win — the loser throws ConcurrencyException, which the
        // native sign-up path treats as "already used" (→ no account created).
        options.Schema.For<RegistrationInviteCode>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.CodeHash)
            .Index(x => x.AppId);

        options.Schema.For<UserChangeRequest>()
            .Identity(x => x.Id)
            .Index(x => x.UserId)
            .Index(x => x.Status);

        // Per-user device sessions (regular Marten document — not event-sourced).
        // Indexed by UserId for the "list my sessions" view and the admin
        // force-logout flow.
        options.Schema.For<UserSession>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.UserId)
            .Index(x => x.ExpiresAt)
            .Index(x => x.AbsoluteExpiresAt);

        options.Schema.For<ClientSession>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.UserId)
            .Index(x => x.ClientId)
            .Index(x => x.AuthorizationId)
            .Index(x => x.ExpiresAt)
            .Index(x => x.AbsoluteExpiresAt);

        // WebAuthn/passkey credentials (raw crypto, not event-sourced). One per
        // enrolled authenticator; indexed by UserId for the per-user list/login
        // lookup and the cascade-delete on permanent erase.
        options.Schema.For<StoredPasskeyCredential>()
            .Identity(x => x.Id)
            .Index(x => x.UserId);

        // ADR-0010 Phase 2 — cookieless WebAuthn assertion ceremony for the
        // native urn:cocoar:passkey grant. Single-use, short-TTL; keyed by the
        // server-generated ceremonyId. Indexed by ExpiresAt for an optional sweep.
        // Optimistic concurrency + a ConsumedAt marker make the single-use
        // guarantee real: the redeem path version-checks its Store, so two
        // concurrent redemptions of one ceremony_id cannot both mint a token
        // (a Delete would not be version-checked).
        options.Schema.For<PasskeyCeremony>()
            .Identity(x => x.Id)
            .Index(x => x.ExpiresAt)
            .UseOptimisticConcurrency(true);

        // ADR-0009 — cookieless WebAuthn ATTESTATION ceremony for native per-client
        // passkey enrollment (Bearer-authenticated; a native client has no session
        // to stash CredentialCreateOptions). Single-use, short-TTL; same sweep index.
        options.Schema.For<PasskeyEnrollCeremony>()
            .Identity(x => x.Id)
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

        // Realm-owned security event store. The absence of a Realm column is
        // deliberate: physical database ownership is the isolation boundary.
        options.Schema.For<Modgud.Infrastructure.Audit.RealmSecurityAuditEvent>()
            .Identity(x => x.Id)
            .Index(x => x.Timestamp)
            .Index(x => x.EventType);
        options.Schema.For<Modgud.Infrastructure.Audit.RealmAuditFingerprintKey>()
            .Identity(x => x.Id);

        // Tenant-scoped singleton config doc. One row per tenant DB,
        // addressed by the fixed `RealmSettings.SingletonId`. Owned by
        // the realm-admin via /api/admin/realm-settings.
        options.Schema.For<RealmSettingsDoc>()
            .Identity(x => x.Id);

        // ADR-0011 — per-Application config overrides. Tenant-scoped, one row
        // per App that has any override, keyed by App.Id. Lazy-created on first
        // admin write; absence = the App inherits every realm setting (zero
        // migration for existing realms). Merged over RealmSettings by
        // EffectiveSettings/IApplicationSettingsResolver.
        options.Schema.For<Modgud.Domain.Applications.ApplicationSettings>()
            .Identity(x => x.Id);

        // Tenant-scoped singleton — per-realm SAML SP certificate state
        // (PFX bytes DataProtection-encrypted, active + previous slots for
        // rotation overlap). Lazily generated by SamlSpCertificateService.
        options.Schema.For<SamlSpCertificateDocument>()
            .Identity(x => x.Id);

        // SAML request correlation — the AuthnRequests we've issued and are still
        // willing to accept a Response for. Same one-time-use reasoning as
        // MagicLinkChallenge/EmailOtpChallenge: the consume is a version-checked
        // Store of ConsumedAt (Marten does NOT version-check deletes), so two
        // concurrent presentations of one captured Response cannot both sign in.
        // Indexed on ExpiresAt for the opportunistic prune on each new request.
        options.Schema.For<SamlPendingAuthnRequest>()
            .Identity(x => x.Id)
            .UseOptimisticConcurrency(true)
            .Index(x => x.ExpiresAt);

        // Identity events
        options.Events.MapEventType<UserIdentitySetupEvent>("user_identity_setup");
        options.Events.MapEventType<UserUserNameChangedEvent>("user_username_changed");
        options.Events.MapEventType<UserPasswordChangedEvent>("user_password_changed");
        options.Events.MapEventType<UserLoggedInEvent>("user_logged_in");
        options.Events.MapEventType<UserLoginFailedEvent>("user_login_failed");
        options.Events.MapEventType<UserLoginFailuresObservedEvent>("user_login_failures_observed");
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

        // IP addresses are PII under GDPR — strip them from login records. The
        // login method is non-PII (a bounded code), so it passes through the mask.
        options.Events.AddMaskingRuleForProtectedInformation<UserLoggedInEvent>(e =>
            new UserLoggedInEvent(e.UserId, IpAddress: null, e.Method));

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

        // Human principals are projected directly into the concrete Person subtype.
        // GroupProjection is owned and registered by Modgud.Authorization; Marten's
        // subclass mapping stores both in the shared Principal document table.
        options.Projections.Add<PersonProjection>(ProjectionLifecycle.Inline);

        // View projections — composite async. Single stage now that the IdP-only
        // surface only has UserView; kept as a composite so adopters can append
        // their own app-specific views as additional stages without rewriting wiring.
        options.Projections.CompositeProjectionFor("ViewProjections", projection =>
        {
            projection.Add<UserViewProjection>();
            projection.Add<Modgud.Infrastructure.Persistence.Marten.Projections.Inbox.InboxItemProjection>();
        });

        // Tenant audit read model — a flat, per-event projection (one row per
        // audited event) over the user + config streams; queried per realm by the
        // GDPR-audit read surface (Phase 2). Deliberately an EventProjection, not an
        // aggregation: an audit log is a list of occurrences, not a per-aggregate
        // snapshot. See the maintainers' 'logging-audit-redesign' design note §A.3.
        options.Projections.Add<Modgud.Authentication.Audit.AuthAuditViewProjection>(ProjectionLifecycle.Async);

        return options;
    }
}
