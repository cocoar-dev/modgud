using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.OAuth;

public class OAuthApplicationStateProjection : SingleStreamProjection<OAuthApplicationState, Guid>
{
    public OAuthApplicationState Create(OAuthApplicationCreated e) => new()
    {
        Id = e.ApplicationId,
        ClientId = e.ClientId,
        DisplayName = e.DisplayName,
        ClientType = e.ClientType,
        ConsentType = e.ConsentType,
        ApplicationType = e.ApplicationType,
        RedirectUris = e.RedirectUris.ToList(),
        PostLogoutRedirectUris = e.PostLogoutRedirectUris.ToList(),
        Permissions = e.Permissions.ToList(),
        Requirements = e.Requirements.ToList(),
    };

    public void Apply(OAuthApplicationDisplayNameChanged e, OAuthApplicationState s) => s.DisplayName = e.DisplayName;
    public void Apply(OAuthApplicationClientTypeChanged e, OAuthApplicationState s) => s.ClientType = e.ClientType;
    public void Apply(OAuthApplicationConsentTypeChanged e, OAuthApplicationState s) => s.ConsentType = e.ConsentType;
    public void Apply(OAuthApplicationRedirectUrisChanged e, OAuthApplicationState s) => s.RedirectUris = e.RedirectUris.ToList();
    public void Apply(OAuthApplicationPostLogoutRedirectUrisChanged e, OAuthApplicationState s) => s.PostLogoutRedirectUris = e.PostLogoutRedirectUris.ToList();
    public void Apply(OAuthApplicationPermissionsChanged e, OAuthApplicationState s) => s.Permissions = e.Permissions.ToList();
    public void Apply(OAuthApplicationRequirementsChanged e, OAuthApplicationState s) => s.Requirements = e.Requirements.ToList();

    public void Apply(OAuthApplicationSettingsChanged e, OAuthApplicationState s)
    {
        s.Settings = new Dictionary<string, string>(e.Settings);

        if (s.Settings.TryGetValue(OAuthApplicationSettingKeys.AccessTokenType, out var v) &&
            Enum.TryParse<AccessTokenType>(v, out var parsed))
        {
            s.AccessTokenType = parsed;
        }
    }

    public void Apply(OAuthApplicationDisplayNamesChanged e, OAuthApplicationState s) => s.DisplayNames = new Dictionary<string, string>(e.DisplayNames);
    public void Apply(OAuthApplicationPropertiesChanged e, OAuthApplicationState s) => s.Properties = new Dictionary<string, object?>(e.Properties);
    public void Apply(OAuthApplicationDeleted e, OAuthApplicationState s) => s.IsDeleted = true;
}
