namespace Cocoar.Auth.Domain.OAuth.Applications;

/// <summary>
/// Event-sourced aggregate for OAuth applications (clients).
/// Sensitive data (ClientSecret, JsonWebKeySet) lives in
/// <see cref="OAuthApplicationSecurityData"/>, NOT in events.
/// </summary>
public class OAuthApplicationAggregate
{
    public Guid Id { get; private set; }
    public string ClientId { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string? ClientType { get; private set; }
    public string? ConsentType { get; private set; }
    public string? ApplicationType { get; private set; }
    public List<string> RedirectUris { get; private set; } = new();
    public List<string> PostLogoutRedirectUris { get; private set; } = new();
    public List<string> Permissions { get; private set; } = new();
    public List<string> Requirements { get; private set; } = new();
    public Dictionary<string, string> Settings { get; private set; } = new();
    public Dictionary<string, string> DisplayNames { get; private set; } = new();
    public Dictionary<string, object?> Properties { get; private set; } = new();
    /// <summary>
    /// n:m link to Applications. Empty = realm-wide / unassigned. One id =
    /// typical app-scoped SPA. Many ids = a frontend that bundles multiple
    /// resource servers (Keycloak-style <c>resource_access</c> in the
    /// issued tokens).
    /// </summary>
    public List<Guid> AppIds { get; private set; } = [];
    public bool IsDeleted { get; private set; }

    public OAuthApplicationAggregate() { }

    public static (OAuthApplicationAggregate, OAuthApplicationCreated) Create(
        Guid id,
        string clientId,
        string? displayName,
        string? clientType,
        string? consentType,
        string? applicationType,
        IReadOnlyList<string> redirectUris,
        IReadOnlyList<string> postLogoutRedirectUris,
        IReadOnlyList<string> permissions,
        IReadOnlyList<string> requirements)
    {
        var aggregate = new OAuthApplicationAggregate();
        var @event = new OAuthApplicationCreated(
            id, clientId, displayName, clientType, consentType, applicationType,
            redirectUris, postLogoutRedirectUris, permissions, requirements);
        aggregate.Apply(@event);
        return (aggregate, @event);
    }

    public OAuthApplicationDisplayNameChanged SetDisplayName(string? displayName)
    {
        var e = new OAuthApplicationDisplayNameChanged(Id, displayName);
        Apply(e);
        return e;
    }

    public OAuthApplicationClientTypeChanged SetClientType(string? clientType)
    {
        var e = new OAuthApplicationClientTypeChanged(Id, clientType);
        Apply(e);
        return e;
    }

    public OAuthApplicationConsentTypeChanged SetConsentType(string? consentType)
    {
        var e = new OAuthApplicationConsentTypeChanged(Id, consentType);
        Apply(e);
        return e;
    }

    public OAuthApplicationRedirectUrisChanged SetRedirectUris(IReadOnlyList<string> redirectUris)
    {
        var e = new OAuthApplicationRedirectUrisChanged(Id, redirectUris);
        Apply(e);
        return e;
    }

    public OAuthApplicationPostLogoutRedirectUrisChanged SetPostLogoutRedirectUris(IReadOnlyList<string> uris)
    {
        var e = new OAuthApplicationPostLogoutRedirectUrisChanged(Id, uris);
        Apply(e);
        return e;
    }

    public OAuthApplicationPermissionsChanged SetPermissions(IReadOnlyList<string> permissions)
    {
        var e = new OAuthApplicationPermissionsChanged(Id, permissions);
        Apply(e);
        return e;
    }

    public OAuthApplicationRequirementsChanged SetRequirements(IReadOnlyList<string> requirements)
    {
        var e = new OAuthApplicationRequirementsChanged(Id, requirements);
        Apply(e);
        return e;
    }

    public OAuthApplicationSettingsChanged SetSettings(IReadOnlyDictionary<string, string> settings)
    {
        var e = new OAuthApplicationSettingsChanged(Id, settings);
        Apply(e);
        return e;
    }

    public OAuthApplicationDisplayNamesChanged SetDisplayNames(IReadOnlyDictionary<string, string> displayNames)
    {
        var e = new OAuthApplicationDisplayNamesChanged(Id, displayNames);
        Apply(e);
        return e;
    }

    public OAuthApplicationPropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> properties)
    {
        var e = new OAuthApplicationPropertiesChanged(Id, properties);
        Apply(e);
        return e;
    }

    public OAuthApplicationAppIdsChanged SetAppIds(IReadOnlyList<Guid> appIds)
    {
        var e = new OAuthApplicationAppIdsChanged(Id, appIds);
        Apply(e);
        return e;
    }

    public OAuthApplicationDeleted Delete()
    {
        var e = new OAuthApplicationDeleted(Id);
        Apply(e);
        return e;
    }

    public void Apply(OAuthApplicationCreated @event)
    {
        Id = @event.ApplicationId;
        ClientId = @event.ClientId;
        DisplayName = @event.DisplayName;
        ClientType = @event.ClientType;
        ConsentType = @event.ConsentType;
        ApplicationType = @event.ApplicationType;
        RedirectUris = @event.RedirectUris.ToList();
        PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList();
        Permissions = @event.Permissions.ToList();
        Requirements = @event.Requirements.ToList();
    }

    public void Apply(OAuthApplicationDisplayNameChanged @event) => DisplayName = @event.DisplayName;
    public void Apply(OAuthApplicationClientTypeChanged @event) => ClientType = @event.ClientType;
    public void Apply(OAuthApplicationConsentTypeChanged @event) => ConsentType = @event.ConsentType;
    public void Apply(OAuthApplicationRedirectUrisChanged @event) => RedirectUris = @event.RedirectUris.ToList();
    public void Apply(OAuthApplicationPostLogoutRedirectUrisChanged @event) => PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList();
    public void Apply(OAuthApplicationPermissionsChanged @event) => Permissions = @event.Permissions.ToList();
    public void Apply(OAuthApplicationRequirementsChanged @event) => Requirements = @event.Requirements.ToList();
    public void Apply(OAuthApplicationSettingsChanged @event) => Settings = new Dictionary<string, string>(@event.Settings);
    public void Apply(OAuthApplicationDisplayNamesChanged @event) => DisplayNames = new Dictionary<string, string>(@event.DisplayNames);
    public void Apply(OAuthApplicationPropertiesChanged @event) => Properties = new Dictionary<string, object?>(@event.Properties);

    /// <summary>
    /// Legacy stream-replay hook. Maps the old single-app event to the n:m
    /// state: <c>AppId == null</c> empties the list, otherwise it becomes
    /// the singleton list. Never emitted by new writes.
    /// </summary>
    public void Apply(OAuthApplicationAppIdChanged @event)
        => AppIds = @event.AppId is Guid id ? [id] : [];

    public void Apply(OAuthApplicationAppIdsChanged @event) => AppIds = [.. @event.AppIds];

    public void Apply(OAuthApplicationDeleted @event) => IsDeleted = true;
}
