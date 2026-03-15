using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Application.DTOs.LoginProviders;

/// <summary>
/// DTO for returning login provider information.
/// </summary>
public record LoginProviderDto
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public required LoginProviderType Type { get; init; }
	public required Dictionary<string, string> Configuration { get; init; }
	public bool IsBuiltIn { get; init; }
}

/// <summary>
/// DTO for creating a new login provider.
/// </summary>
public record CreateLoginProviderDto
{
	/// <summary>
	/// The unique name for this login provider.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this login provider.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// The type of login provider (Internal, OpenIdConnect).
	/// </summary>
	public LoginProviderType Type { get; init; } = LoginProviderType.Internal;

	/// <summary>
	/// Configuration settings (e.g., Authority, ClientId for OIDC providers).
	/// </summary>
	public Dictionary<string, string> Configuration { get; init; } = new();
}

/// <summary>
/// DTO for updating a login provider.
/// </summary>
public record UpdateLoginProviderDto
{
	/// <summary>
	/// The name of the login provider.
	/// </summary>
	public string? Name { get; init; }

	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this login provider.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Configuration settings.
	/// </summary>
	public Dictionary<string, string>? Configuration { get; init; }
}

/// <summary>
/// DTO for a list of login providers.
/// </summary>
public record LoginProviderListDto
{
	public required List<LoginProviderDto> Items { get; init; }
	public int TotalCount { get; init; }
}
