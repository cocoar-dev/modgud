namespace Cocoar.Auth.Domain.Events;

// ═══════════════════════════════════════════════════════════════════════════
// LOGIN PROVIDER LIFECYCLE EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Event raised when a new login provider is created.
/// </summary>
public record LoginProviderCreated(
	Guid LoginProviderId,
	string Name,
	string? DisplayName,
	string? Description,
	LoginProviderType Type,
	Dictionary<string, string> Configuration,
	bool IsBuiltIn);

/// <summary>
/// Event raised when a login provider's name is changed.
/// </summary>
public record LoginProviderNameChanged(
	Guid LoginProviderId,
	string NewName);

/// <summary>
/// Event raised when a login provider's display name is changed.
/// </summary>
public record LoginProviderDisplayNameChanged(
	Guid LoginProviderId,
	string? NewDisplayName);

/// <summary>
/// Event raised when a login provider's description is changed.
/// </summary>
public record LoginProviderDescriptionChanged(
	Guid LoginProviderId,
	string? NewDescription);

/// <summary>
/// Event raised when a login provider's configuration is changed.
/// </summary>
public record LoginProviderConfigurationChanged(
	Guid LoginProviderId,
	Dictionary<string, string> NewConfiguration);

/// <summary>
/// Event raised when a login provider is deleted (soft delete).
/// </summary>
public record LoginProviderDeleted(
	Guid LoginProviderId);

/// <summary>
/// The type of login provider.
/// </summary>
public enum LoginProviderType
{
	/// <summary>
	/// Internal password-based authentication.
	/// </summary>
	Internal = 0,

	/// <summary>
	/// External OpenID Connect provider.
	/// </summary>
	OpenIdConnect = 1
}
