namespace Cocoar.Auth.Domain.Events;

/// <summary>
/// Event raised when an OAuth application is created.
/// </summary>
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

/// <summary>
/// Event raised when an OAuth application's display name is changed.
/// </summary>
public record OAuthApplicationDisplayNameChanged(
	Guid ApplicationId,
	string? DisplayName);

/// <summary>
/// Event raised when an OAuth application's client type is changed.
/// </summary>
public record OAuthApplicationClientTypeChanged(
	Guid ApplicationId,
	string? ClientType);

/// <summary>
/// Event raised when an OAuth application's consent type is changed.
/// </summary>
public record OAuthApplicationConsentTypeChanged(
	Guid ApplicationId,
	string? ConsentType);

/// <summary>
/// Event raised when an OAuth application's redirect URIs are changed.
/// </summary>
public record OAuthApplicationRedirectUrisChanged(
	Guid ApplicationId,
	IReadOnlyList<string> RedirectUris);

/// <summary>
/// Event raised when an OAuth application's post-logout redirect URIs are changed.
/// </summary>
public record OAuthApplicationPostLogoutRedirectUrisChanged(
	Guid ApplicationId,
	IReadOnlyList<string> PostLogoutRedirectUris);

/// <summary>
/// Event raised when an OAuth application's permissions are changed.
/// </summary>
public record OAuthApplicationPermissionsChanged(
	Guid ApplicationId,
	IReadOnlyList<string> Permissions);

/// <summary>
/// Event raised when an OAuth application's requirements are changed.
/// </summary>
public record OAuthApplicationRequirementsChanged(
	Guid ApplicationId,
	IReadOnlyList<string> Requirements);

/// <summary>
/// Event raised when an OAuth application's settings are changed.
/// </summary>
public record OAuthApplicationSettingsChanged(
	Guid ApplicationId,
	IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Event raised when an OAuth application's localized display names are changed.
/// </summary>
public record OAuthApplicationDisplayNamesChanged(
	Guid ApplicationId,
	IReadOnlyDictionary<string, string> DisplayNames);

/// <summary>
/// Event raised when an OAuth application's properties are changed.
/// </summary>
public record OAuthApplicationPropertiesChanged(
	Guid ApplicationId,
	IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Event raised when an OAuth application is deleted.
/// </summary>
public record OAuthApplicationDeleted(Guid ApplicationId);
