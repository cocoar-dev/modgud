namespace Cocoar.Auth.Domain.OAuth.Scopes;

/// <summary>
/// Event-sourced aggregate for OAuth scopes. No sensitive data — everything
/// stays in the event stream.
/// </summary>
public class OAuthScopeAggregate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string? Description { get; private set; }
    public List<string> Resources { get; private set; } = new();
    public Dictionary<string, string> DisplayNames { get; private set; } = new();
    public Dictionary<string, string> Descriptions { get; private set; } = new();
    public Dictionary<string, object?> Properties { get; private set; } = new();
    public bool Enabled { get; private set; } = true;
    public bool Required { get; private set; }
    public bool Emphasize { get; private set; }
    public bool ShowInDiscoveryDocument { get; private set; } = true;
    public List<string> UserClaims { get; private set; } = new();
    public bool IsDeleted { get; private set; }

    public OAuthScopeAggregate() { }

    public static (OAuthScopeAggregate, OAuthScopeCreated) Create(
        Guid id, string name, string? displayName, string? description, IReadOnlyList<string> resources)
    {
        var aggregate = new OAuthScopeAggregate();
        var e = new OAuthScopeCreated(id, name, displayName, description, resources);
        aggregate.Apply(e);
        return (aggregate, e);
    }

    public OAuthScopeDisplayNameChanged SetDisplayName(string? v) { var e = new OAuthScopeDisplayNameChanged(Id, v); Apply(e); return e; }
    public OAuthScopeDescriptionChanged SetDescription(string? v) { var e = new OAuthScopeDescriptionChanged(Id, v); Apply(e); return e; }
    public OAuthScopeResourcesChanged SetResources(IReadOnlyList<string> v) { var e = new OAuthScopeResourcesChanged(Id, v); Apply(e); return e; }
    public OAuthScopeDisplayNamesChanged SetDisplayNames(IReadOnlyDictionary<string, string> v) { var e = new OAuthScopeDisplayNamesChanged(Id, v); Apply(e); return e; }
    public OAuthScopeDescriptionsChanged SetDescriptions(IReadOnlyDictionary<string, string> v) { var e = new OAuthScopeDescriptionsChanged(Id, v); Apply(e); return e; }
    public OAuthScopePropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> v) { var e = new OAuthScopePropertiesChanged(Id, v); Apply(e); return e; }
    public OAuthScopeEnabledChanged SetEnabled(bool v) { var e = new OAuthScopeEnabledChanged(Id, v); Apply(e); return e; }
    public OAuthScopeRequiredChanged SetRequired(bool v) { var e = new OAuthScopeRequiredChanged(Id, v); Apply(e); return e; }
    public OAuthScopeEmphasizeChanged SetEmphasize(bool v) { var e = new OAuthScopeEmphasizeChanged(Id, v); Apply(e); return e; }
    public OAuthScopeShowInDiscoveryDocumentChanged SetShowInDiscoveryDocument(bool v) { var e = new OAuthScopeShowInDiscoveryDocumentChanged(Id, v); Apply(e); return e; }
    public OAuthScopeUserClaimsChanged SetUserClaims(IReadOnlyList<string> v) { var e = new OAuthScopeUserClaimsChanged(Id, v); Apply(e); return e; }
    public OAuthScopeDeleted Delete() { var e = new OAuthScopeDeleted(Id); Apply(e); return e; }

    public void Apply(OAuthScopeCreated e)
    {
        Id = e.ScopeId; Name = e.Name; DisplayName = e.DisplayName; Description = e.Description;
        Resources = e.Resources.ToList();
    }
    public void Apply(OAuthScopeDisplayNameChanged e) => DisplayName = e.DisplayName;
    public void Apply(OAuthScopeDescriptionChanged e) => Description = e.Description;
    public void Apply(OAuthScopeResourcesChanged e) => Resources = e.Resources.ToList();
    public void Apply(OAuthScopeDisplayNamesChanged e) => DisplayNames = new Dictionary<string, string>(e.DisplayNames);
    public void Apply(OAuthScopeDescriptionsChanged e) => Descriptions = new Dictionary<string, string>(e.Descriptions);
    public void Apply(OAuthScopePropertiesChanged e) => Properties = new Dictionary<string, object?>(e.Properties);
    public void Apply(OAuthScopeEnabledChanged e) => Enabled = e.Enabled;
    public void Apply(OAuthScopeRequiredChanged e) => Required = e.Required;
    public void Apply(OAuthScopeEmphasizeChanged e) => Emphasize = e.Emphasize;
    public void Apply(OAuthScopeShowInDiscoveryDocumentChanged e) => ShowInDiscoveryDocument = e.ShowInDiscoveryDocument;
    public void Apply(OAuthScopeUserClaimsChanged e) => UserClaims = e.UserClaims.ToList();
    public void Apply(OAuthScopeDeleted e) => IsDeleted = true;
}
