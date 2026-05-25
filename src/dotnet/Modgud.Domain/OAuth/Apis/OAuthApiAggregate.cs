namespace Modgud.Domain.OAuth.Apis;

/// <summary>
/// Event-sourced aggregate for an OAuth-protected API (resource server). A
/// resource server is identified by its <see cref="Name"/> (used as the
/// <c>aud</c> claim) and gates on a subset of its linked <see cref="App"/>'s
/// permission catalog. Resource servers authenticate via OAuth
/// (Client-Credentials with a linked ServiceAccount), not via their own
/// shared secret — this aggregate has no credential surface.
/// </summary>
public partial class OAuthApiAggregate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string? Description { get; private set; }
    public bool Enabled { get; private set; } = true;
    public List<string> Scopes { get; private set; } = new();
    public List<string> UserClaims { get; private set; } = new();
    public Dictionary<string, object?> Properties { get; private set; } = new();
    /// <summary>
    /// Optional FK to <c>App.Id</c>. <c>null</c> = unassigned. Resource
    /// servers that authenticate against the distribution API need this
    /// link so the IDP can derive the App context from the authenticated
    /// RS without a query parameter.
    /// </summary>
    public Guid? AppId { get; private set; }

    /// <summary>
    /// Subset of the linked App's permission catalog this RS gates on.
    /// FKs into <c>App.Permissions[].Id</c>. Empty when the RS hasn't been
    /// configured with a subset yet, or when no App is linked.
    /// </summary>
    public List<Guid> PermissionIds { get; private set; } = new();

    public bool IsDeleted { get; private set; }

    public OAuthApiAggregate() { }

    public static (OAuthApiAggregate, OAuthApiCreated) Create(
        Guid id, string name, string? displayName, string? description, bool enabled, IReadOnlyList<string> scopes)
    {
        var aggregate = new OAuthApiAggregate();
        var e = new OAuthApiCreated(id, name, displayName, description, enabled, scopes);
        aggregate.Apply(e);
        return (aggregate, e);
    }

    public OAuthApiDisplayNameChanged SetDisplayName(string? v) { var e = new OAuthApiDisplayNameChanged(Id, v); Apply(e); return e; }
    public OAuthApiDescriptionChanged SetDescription(string? v) { var e = new OAuthApiDescriptionChanged(Id, v); Apply(e); return e; }
    public OAuthApiEnabled Enable() { var e = new OAuthApiEnabled(Id); Apply(e); return e; }
    public OAuthApiDisabled Disable() { var e = new OAuthApiDisabled(Id); Apply(e); return e; }
    public OAuthApiScopesChanged SetScopes(IReadOnlyList<string> v) { var e = new OAuthApiScopesChanged(Id, v); Apply(e); return e; }
    public OAuthApiUserClaimsChanged SetUserClaims(IReadOnlyList<string> v) { var e = new OAuthApiUserClaimsChanged(Id, v); Apply(e); return e; }
    public OAuthApiPropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> v) { var e = new OAuthApiPropertiesChanged(Id, v); Apply(e); return e; }
    public OAuthApiAppIdChanged SetAppId(Guid? v) { var e = new OAuthApiAppIdChanged(Id, v); Apply(e); return e; }
    public OAuthApiPermissionIdsChanged SetPermissionIds(IReadOnlyList<Guid> v) { var e = new OAuthApiPermissionIdsChanged(Id, v); Apply(e); return e; }
    public OAuthApiDeleted Delete() { var e = new OAuthApiDeleted(Id); Apply(e); return e; }

    public void Apply(OAuthApiCreated e)
    {
        Id = e.ApiId; Name = e.Name; DisplayName = e.DisplayName; Description = e.Description;
        Enabled = e.Enabled; Scopes = e.Scopes.ToList();
    }
    public void Apply(OAuthApiDisplayNameChanged e) => DisplayName = e.DisplayName;
    public void Apply(OAuthApiDescriptionChanged e) => Description = e.Description;
    public void Apply(OAuthApiEnabled e) => Enabled = true;
    public void Apply(OAuthApiDisabled e) => Enabled = false;
    public void Apply(OAuthApiScopesChanged e) => Scopes = e.Scopes.ToList();
    public void Apply(OAuthApiUserClaimsChanged e) => UserClaims = e.UserClaims.ToList();
    public void Apply(OAuthApiPropertiesChanged e) => Properties = new Dictionary<string, object?>(e.Properties);
    public void Apply(OAuthApiAppIdChanged e) => AppId = e.AppId;
    public void Apply(OAuthApiPermissionIdsChanged e) => PermissionIds = e.PermissionIds.ToList();
    public void Apply(OAuthApiDeleted e) => IsDeleted = true;
}
