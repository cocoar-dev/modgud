using Cocoar.Auth.Domain.Identity.LoginProviders;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Scopes;
using Cocoar.Auth.Domain.OAuth.Storage;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.LoginProviders;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.OAuth;
using JasperFx.Events.Projections;
using Marten;

namespace Cocoar.Auth.Infrastructure.OAuth;

/// <summary>
/// Marten wiring for the OAuth admin slice (clients, scopes, APIs) plus the
/// adjacent login-provider slice. Registers documents, inline state projections,
/// and stable event-type aliases so renames + namespace moves don't break
/// persisted streams.
///
/// <para>Called from the Authentication slice's <c>UseCocoarAuthAuthentication</c>
/// callback (kept there for now since OAuth has no separate slice project yet).</para>
/// </summary>
public static class OAuthMartenSetup
{
    public static StoreOptions UseCocoarAuthOAuth(this StoreOptions options)
    {
        // Documents — inline state projections + raw security data.
        options.Schema.For<OAuthApplicationState>()
            .Identity(x => x.Id)
            .Index(x => x.ClientId)
            .Index(x => x.AppId)
            .Index(x => x.IsDeleted);

        options.Schema.For<OAuthApplicationSecurityData>()
            .Identity(x => x.Id);

        options.Schema.For<OAuthScopeState>()
            .Identity(x => x.Id)
            .Index(x => x.Name)
            .Index(x => x.IsDeleted);

        options.Schema.For<OAuthApiState>()
            .Identity(x => x.Id)
            .Index(x => x.Name)
            .Index(x => x.IsDeleted);

        options.Schema.For<OAuthApiSecurityData>()
            .Identity(x => x.Id);

        options.Schema.For<LoginProviderState>()
            .Identity(x => x.Id)
            .Index(x => x.Name)
            .Index(x => x.IsDeleted);

        // OpenIddict authorization + token documents — string-id (OpenIddict gives
        // us a Guid-as-string); not event-sourced, ephemeral.
        options.Schema.For<OpenIddictAuthorizationDocument>()
            .Identity(x => x.Id)
            .Index(x => x.Subject)
            .Index(x => x.ApplicationId)
            .Index(x => x.Status);

        options.Schema.For<OpenIddictTokenDocument>()
            .Identity(x => x.Id)
            .Index(x => x.Subject)
            .Index(x => x.ApplicationId)
            .Index(x => x.AuthorizationId)
            .Index(x => x.ReferenceId)
            .Index(x => x.Status);

        // Stable event-type aliases — copied verbatim from the legacy backend so
        // re-using a legacy DB would also work, and so renames are safe forever.
        options.Events.MapEventType<OAuthApplicationCreated>("oauth_application_created");
        options.Events.MapEventType<OAuthApplicationDisplayNameChanged>("oauth_application_display_name_changed");
        options.Events.MapEventType<OAuthApplicationClientTypeChanged>("oauth_application_client_type_changed");
        options.Events.MapEventType<OAuthApplicationConsentTypeChanged>("oauth_application_consent_type_changed");
        options.Events.MapEventType<OAuthApplicationRedirectUrisChanged>("oauth_application_redirect_uris_changed");
        options.Events.MapEventType<OAuthApplicationPostLogoutRedirectUrisChanged>("oauth_application_post_logout_redirect_uris_changed");
        options.Events.MapEventType<OAuthApplicationPermissionsChanged>("oauth_application_permissions_changed");
        options.Events.MapEventType<OAuthApplicationRequirementsChanged>("oauth_application_requirements_changed");
        options.Events.MapEventType<OAuthApplicationSettingsChanged>("oauth_application_settings_changed");
        options.Events.MapEventType<OAuthApplicationDisplayNamesChanged>("oauth_application_display_names_changed");
        options.Events.MapEventType<OAuthApplicationPropertiesChanged>("oauth_application_properties_changed");
        options.Events.MapEventType<OAuthApplicationAppIdChanged>("oauth_application_app_id_changed");
        options.Events.MapEventType<OAuthApplicationDeleted>("oauth_application_deleted");

        options.Events.MapEventType<OAuthScopeCreated>("oauth_scope_created");
        options.Events.MapEventType<OAuthScopeDisplayNameChanged>("oauth_scope_display_name_changed");
        options.Events.MapEventType<OAuthScopeDescriptionChanged>("oauth_scope_description_changed");
        options.Events.MapEventType<OAuthScopeResourcesChanged>("oauth_scope_resources_changed");
        options.Events.MapEventType<OAuthScopeDisplayNamesChanged>("oauth_scope_display_names_changed");
        options.Events.MapEventType<OAuthScopeDescriptionsChanged>("oauth_scope_descriptions_changed");
        options.Events.MapEventType<OAuthScopePropertiesChanged>("oauth_scope_properties_changed");
        options.Events.MapEventType<OAuthScopeEnabledChanged>("oauth_scope_enabled_changed");
        options.Events.MapEventType<OAuthScopeRequiredChanged>("oauth_scope_required_changed");
        options.Events.MapEventType<OAuthScopeEmphasizeChanged>("oauth_scope_emphasize_changed");
        options.Events.MapEventType<OAuthScopeShowInDiscoveryDocumentChanged>("oauth_scope_show_in_discovery_document_changed");
        options.Events.MapEventType<OAuthScopeUserClaimsChanged>("oauth_scope_user_claims_changed");
        options.Events.MapEventType<OAuthScopeDeleted>("oauth_scope_deleted");

        options.Events.MapEventType<OAuthApiCreated>("oauth_api_created");
        options.Events.MapEventType<OAuthApiDisplayNameChanged>("oauth_api_display_name_changed");
        options.Events.MapEventType<OAuthApiDescriptionChanged>("oauth_api_description_changed");
        options.Events.MapEventType<OAuthApiEnabled>("oauth_api_enabled");
        options.Events.MapEventType<OAuthApiDisabled>("oauth_api_disabled");
        options.Events.MapEventType<OAuthApiScopesChanged>("oauth_api_scopes_changed");
        options.Events.MapEventType<OAuthApiUserClaimsChanged>("oauth_api_user_claims_changed");
        options.Events.MapEventType<OAuthApiPropertiesChanged>("oauth_api_properties_changed");
        options.Events.MapEventType<OAuthApiDeleted>("oauth_api_deleted");

        options.Events.MapEventType<LoginProviderCreated>("login_provider_created");
        options.Events.MapEventType<LoginProviderNameChanged>("login_provider_name_changed");
        options.Events.MapEventType<LoginProviderDisplayNameChanged>("login_provider_display_name_changed");
        options.Events.MapEventType<LoginProviderDescriptionChanged>("login_provider_description_changed");
        options.Events.MapEventType<LoginProviderConfigurationChanged>("login_provider_configuration_changed");
        options.Events.MapEventType<LoginProviderDeleted>("login_provider_deleted");

        // Inline projections — admin reads + uniqueness checks need synchronous consistency.
        options.Projections.Add<OAuthApplicationStateProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<OAuthScopeStateProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<OAuthApiStateProjection>(ProjectionLifecycle.Inline);
        options.Projections.Add<LoginProviderStateProjection>(ProjectionLifecycle.Inline);

        return options;
    }
}
