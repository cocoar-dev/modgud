using JasperFx.Events.Projections;
using Marten;
using TimeToDo.Authentication.AuthLog;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Domain.ExternalAuth;
using TimeToDo.Authentication.Domain.ExternalAuth.Events;
using TimeToDo.Authentication.Events;
using TimeToDo.Authentication.Identity.ExternalAuth;
using TimeToDo.Authentication.Projections;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Authentication.Setup;

/// <summary>
/// Marten wiring for the authentication slice. Hardcoded for TimeToDo — when adopting
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
    public static StoreOptions UseTimeTodoAuthentication(this StoreOptions options)
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
        options.Projections.Add<TimeToDoPrincipalProjection>(ProjectionLifecycle.Inline);

        // View projections — composite async. Stage 1 commits before Stage 2 processes,
        // guaranteeing UserView/CustomerView exist before Todo/Comment views are built.
        options.Projections.CompositeProjectionFor("ViewProjections", projection =>
        {
            // Stage 1: Reference data (no cross-dependencies)
            projection.Add<UserViewProjection>();
            projection.Add<CustomerViewProjection>();

            // Stage 2: Business views (depend on UserView/CustomerView from stage 1)
            projection.Add<TodoViewProjection>(2);
            projection.Add<CommentViewProjection>(2);
            projection.Add<CommentReadStatusProjection>(2);
        });

        return options;
    }
}
