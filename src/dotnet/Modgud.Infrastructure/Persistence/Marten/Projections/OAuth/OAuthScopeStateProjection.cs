using Modgud.Domain.OAuth.Scopes;
using Marten.Events.Aggregation;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;

public partial class OAuthScopeStateProjection : SingleStreamProjection<OAuthScopeState, Guid>
{
    // Apply (not Create) so a Created event on an EXISTING stream REVIVES the entity:
    // provisioning re-imports a soft-deleted entity under its pinned id, and the fresh
    // document replaces the old one wholesale (IsDeleted back to false, no stale field).
    public OAuthScopeState Apply(OAuthScopeCreated e, OAuthScopeState _) => new()
    {
        Id = e.ScopeId,
        Name = e.Name,
        DisplayName = e.DisplayName,
        Description = e.Description,
        Resources = e.Resources.ToList(),
    };

    public void Apply(OAuthScopeDisplayNameChanged e, OAuthScopeState s) => s.DisplayName = e.DisplayName;
    public void Apply(OAuthScopeDescriptionChanged e, OAuthScopeState s) => s.Description = e.Description;
    public void Apply(OAuthScopeResourcesChanged e, OAuthScopeState s) => s.Resources = e.Resources.ToList();
    public void Apply(OAuthScopeDisplayNamesChanged e, OAuthScopeState s) => s.DisplayNames = new Dictionary<string, string>(e.DisplayNames);
    public void Apply(OAuthScopeDescriptionsChanged e, OAuthScopeState s) => s.Descriptions = new Dictionary<string, string>(e.Descriptions);
    public void Apply(OAuthScopePropertiesChanged e, OAuthScopeState s) => s.Properties = new Dictionary<string, object?>(e.Properties);
    public void Apply(OAuthScopeEnabledChanged e, OAuthScopeState s) => s.Enabled = e.Enabled;
    public void Apply(OAuthScopeRequiredChanged e, OAuthScopeState s) => s.Required = e.Required;
    public void Apply(OAuthScopeEmphasizeChanged e, OAuthScopeState s) => s.Emphasize = e.Emphasize;
    public void Apply(OAuthScopeShowInDiscoveryDocumentChanged e, OAuthScopeState s) => s.ShowInDiscoveryDocument = e.ShowInDiscoveryDocument;
    public void Apply(OAuthScopeUserClaimsChanged e, OAuthScopeState s) => s.UserClaims = e.UserClaims.ToList();
    public void Apply(OAuthScopeAppIdChanged e, OAuthScopeState s) => s.AppId = e.AppId;
    public void Apply(OAuthScopeDeleted e, OAuthScopeState s) => s.IsDeleted = true;
}
