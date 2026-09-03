using Modgud.Domain.OAuth.Apis;
using Marten.Events.Aggregation;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;

public partial class OAuthApiStateProjection : SingleStreamProjection<OAuthApiState, Guid>
{
    // Apply (not Create) so a Created event on an EXISTING stream REVIVES the entity:
    // provisioning re-imports a soft-deleted entity under its pinned id, and the fresh
    // document replaces the old one wholesale (IsDeleted back to false, no stale field).
    public OAuthApiState Apply(OAuthApiCreated e, OAuthApiState _) => new()
    {
        Id = e.ApiId,
        Name = e.Name,
        DisplayName = e.DisplayName,
        Description = e.Description,
        Enabled = e.Enabled,
        Scopes = e.Scopes.ToList(),
    };

    public void Apply(OAuthApiDisplayNameChanged e, OAuthApiState s) => s.DisplayName = e.DisplayName;
    public void Apply(OAuthApiDescriptionChanged e, OAuthApiState s) => s.Description = e.Description;
    public void Apply(OAuthApiEnabled e, OAuthApiState s) => s.Enabled = true;
    public void Apply(OAuthApiDisabled e, OAuthApiState s) => s.Enabled = false;
    public void Apply(OAuthApiScopesChanged e, OAuthApiState s) => s.Scopes = e.Scopes.ToList();
    public void Apply(OAuthApiUserClaimsChanged e, OAuthApiState s) => s.UserClaims = e.UserClaims.ToList();
    public void Apply(OAuthApiPropertiesChanged e, OAuthApiState s) => s.Properties = new Dictionary<string, object?>(e.Properties);
    public void Apply(OAuthApiAppIdChanged e, OAuthApiState s) => s.AppId = e.AppId;
    public void Apply(OAuthApiPermissionIdsChanged e, OAuthApiState s) => s.PermissionIds = e.PermissionIds.ToList();
    public void Apply(OAuthApiDeleted e, OAuthApiState s) => s.IsDeleted = true;
}
