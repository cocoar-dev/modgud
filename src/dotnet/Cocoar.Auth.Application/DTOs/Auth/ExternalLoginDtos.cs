namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// DTO representing an available external login provider (no secrets).
/// </summary>
public record ExternalProviderDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public required string Type { get; init; }
}

/// <summary>
/// DTO for a list of available external login providers.
/// </summary>
public record ExternalProviderListDto
{
    public required List<ExternalProviderDto> Providers { get; init; }
}

/// <summary>
/// DTO returned when initiating an external login redirect.
/// </summary>
public record ExternalLoginRedirectDto
{
    public required string RedirectUrl { get; init; }
}

/// <summary>
/// DTO representing a linked external login on a user's profile.
/// </summary>
public record LinkedExternalLoginDto
{
    public required string ProviderName { get; init; }
    public string? ProviderDisplayName { get; init; }
}

/// <summary>
/// DTO for a list of linked external logins.
/// </summary>
public record LinkedExternalLoginListDto
{
    public required List<LinkedExternalLoginDto> Logins { get; init; }
}
