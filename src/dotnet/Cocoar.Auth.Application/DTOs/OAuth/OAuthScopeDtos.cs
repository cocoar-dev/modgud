namespace Cocoar.Auth.Application.DTOs.OAuth;

public record OAuthScopeDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public List<string> Resources { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; }
    public bool Emphasize { get; init; }
    public bool ShowInDiscoveryDocument { get; init; } = true;
    public List<string> UserClaims { get; init; } = [];
    /// <summary>FK to <c>App.Id</c>. Null = global / standard OIDC scope.</summary>
    public string? AppId { get; init; }
    /// <summary>
    /// True for the five OIDC standard scopes (<c>openid</c>, <c>email</c>,
    /// <c>profile</c>, <c>roles</c>, <c>offline_access</c>) — shipped with
    /// the IdP, not editable. The admin UI uses this to render those rows
    /// dimmed.
    /// </summary>
    public bool IsStandard { get; init; }
}

public record CreateOAuthScopeDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public List<string> Resources { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; }
    public bool Emphasize { get; init; }
    public bool ShowInDiscoveryDocument { get; init; } = true;
    public List<string> UserClaims { get; init; } = [];
    /// <summary>App.Id (Guid string). Null/missing = global scope.</summary>
    public string? AppId { get; init; }
}

public record UpdateOAuthScopeDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public List<string>? Resources { get; init; }
    public bool? Enabled { get; init; }
    public bool? Required { get; init; }
    public bool? Emphasize { get; init; }
    public bool? ShowInDiscoveryDocument { get; init; }
    public List<string>? UserClaims { get; init; }
    /// <summary>
    /// PATCH semantics: null/missing = no change, "" = make global,
    /// "<guid>" = assign / change.
    /// </summary>
    public string? AppId { get; init; }
}

public record OAuthScopeListDto
{
    public required List<OAuthScopeDto> Items { get; init; }
    public int TotalCount { get; init; }
}
