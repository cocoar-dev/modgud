namespace Cocoar.Auth.Domain.OAuth.Applications;

public record OAuthApplicationCreated(
    Guid ApplicationId,
    string ClientId,
    string? DisplayName,
    string? ClientType,
    string? ConsentType,
    string? ApplicationType,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Requirements);

public record OAuthApplicationDisplayNameChanged(Guid ApplicationId, string? DisplayName);

/// <summary>
/// Sets the link between this OAuth client and an Application. <c>null</c>
/// detaches the client (it then has no app context — useful for legacy /
/// realm-wide tools). Realm-admin endpoints validate that the AppId exists
/// and is non-deleted at the time the event is appended.
/// </summary>
public record OAuthApplicationAppIdChanged(Guid ApplicationId, Guid? AppId);

public record OAuthApplicationClientTypeChanged(Guid ApplicationId, string? ClientType);

public record OAuthApplicationConsentTypeChanged(Guid ApplicationId, string? ConsentType);

public record OAuthApplicationRedirectUrisChanged(Guid ApplicationId, IReadOnlyList<string> RedirectUris);

public record OAuthApplicationPostLogoutRedirectUrisChanged(Guid ApplicationId, IReadOnlyList<string> PostLogoutRedirectUris);

public record OAuthApplicationPermissionsChanged(Guid ApplicationId, IReadOnlyList<string> Permissions);

public record OAuthApplicationRequirementsChanged(Guid ApplicationId, IReadOnlyList<string> Requirements);

public record OAuthApplicationSettingsChanged(Guid ApplicationId, IReadOnlyDictionary<string, string> Settings);

public record OAuthApplicationDisplayNamesChanged(Guid ApplicationId, IReadOnlyDictionary<string, string> DisplayNames);

public record OAuthApplicationPropertiesChanged(Guid ApplicationId, IReadOnlyDictionary<string, object?> Properties);

public record OAuthApplicationDeleted(Guid ApplicationId);
