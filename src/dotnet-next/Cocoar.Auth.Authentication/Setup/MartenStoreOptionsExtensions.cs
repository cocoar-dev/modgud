using JasperFx.Events.Projections;
using Marten;
using Cocoar.Auth.Authentication.AuthLog;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Projections;

namespace Cocoar.Auth.Authentication.Setup;

/// <summary>
/// Marten wiring for the authentication slice. Hardcoded for Cocoar.Auth — when adopting
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
    public static StoreOptions UseCocoarAuthAuthentication(this StoreOptions options)
    {
        // Identity documents
        options.Schema.For<ApplicationUser>()
            .Identity(x => x.Id)
            .UniqueIndex(x => x.NormalizedUserName)
            .Index(x => x.NormalizedEmail);

        options.Schema.For<UserSecurityData>()
            .Identity(x => x.Id);

        options.Schema.For<EmailOtpChallenge>()
            .Identity(x => x.Id);

        options.Schema.For<MagicLinkChallenge>()
            .Identity(x => x.Id)
            .Index(x => x.UserId);

        options.Schema.For<UserChangeRequest>()
            .Identity(x => x.Id)
            .Index(x => x.UserId)
            .Index(x => x.Status);

        options.Schema.For<IdpConfig>()
            .Identity(x => x.Id)
            .Index(x => x.Flavor)
            .Index(x => x.Enabled)
            .Index(x => x.IsDeleted);

        options.Schema.For<ExternalIdentityLink>()
            .Identity(x => x.Id)
            .UniqueIndex(x => x.Issuer, x => x.Subject)
            .Index(x => x.UserId)
            .Index(x => x.IdpConfigId)
            .Index(x => x.IsUnlinked);

        options.Schema.For<AuthLogDocument>()
            .DatabaseSchemaName("marten")
            .Identity(x => x.Id)
            .Index(x => x.Timestamp);

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

        // IdP config events
        options.Events.MapEventType<IdpConfigAddedEvent>("idp_config_added");
        options.Events.MapEventType<IdpConfigUpdatedEvent>("idp_config_updated");
        options.Events.MapEventType<IdpConfigSecretRotatedEvent>("idp_config_secret_rotated");
        options.Events.MapEventType<IdpConfigEnabledEvent>("idp_config_enabled");
        options.Events.MapEventType<IdpConfigDisabledEvent>("idp_config_disabled");
        options.Events.MapEventType<IdpConfigDeletedEvent>("idp_config_deleted");

        // External identity link events
        options.Events.MapEventType<ExternalIdentityLinkedEvent>("external_identity_linked");
        options.Events.MapEventType<ExternalIdentityScriptRecordedEvent>("external_identity_script_recorded");
        options.Events.MapEventType<ExternalIdentityUnlinkedEvent>("external_identity_unlinked");

        // User-stream mirror events for external auth
        options.Events.MapEventType<UserExternalIdentityLinkedEvent>("user_external_identity_linked");
        options.Events.MapEventType<UserExternalIdentityUnlinkedEvent>("user_external_identity_unlinked");

        // External-auth projections — inline (login flow reads these synchronously)
        options.Projections.Add<IdpConfigProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<ExternalIdentityLinkProjection>(ProjectionLifecycle.Inline);

        // Unified principal projection — Person+Group doc, inline for synchronously-consistent
        // permission checks + auto-membership eval.
        options.Projections.Add<CocoarAuthPrincipalProjection>(ProjectionLifecycle.Inline);

        // View projections — composite async. Single stage now that the IdP-only
        // surface only has UserView; kept as a composite so adopters can append
        // their own app-specific views as additional stages without rewriting wiring.
        options.Projections.CompositeProjectionFor("ViewProjections", projection =>
        {
            projection.Add<UserViewProjection>();
        });

        return options;
    }
}
