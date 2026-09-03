using Modgud.Domain.Common;

namespace Modgud.Application.DTOs.OAuth;

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

    /// <summary>
    /// When <c>true</c>, this scope is requestable by clients minted via
    /// Dynamic Client Registration. Off by default. The other half of the
    /// triple opt-in alongside <c>OAuthApi.AllowDynamicRegistration</c>:
    /// gates which capabilities DCR clients can ever ask for.
    /// </summary>
    public bool AllowDynamicRegistrationClients { get; init; }
}

public record CreateOAuthScopeDto
{
    /// <summary>Optional pinned entity id (Guid or ShortGuid) — provisioning
    /// only: a manifest apply pins the exported id at create so ids stay
    /// stable across environments. Server-generated when omitted; a taken id
    /// is a conflict.</summary>
    public string? Id { get; init; }

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

    /// <summary>Per-scope DCR opt-in. Default <c>false</c>.</summary>
    public bool AllowDynamicRegistrationClients { get; init; }
}

/// <summary>Merge-patch update (v2 semantics): absent = unchanged, explicit
/// <c>null</c> clears, <c>[]</c> clears a list; booleans have no clear.</summary>
public record UpdateOAuthScopeDto
{
    public Optional<string?> DisplayName { get; init; }
    public Optional<string?> Description { get; init; }
    public List<string>? Resources { get; init; }
    public bool? Enabled { get; init; }
    public bool? Required { get; init; }
    public bool? Emphasize { get; init; }
    public bool? ShowInDiscoveryDocument { get; init; }
    public List<string>? UserClaims { get; init; }
    /// <summary>
    /// v2 merge-patch: absent = no change; explicit null (or "") = detach
    /// (make global); "&lt;guid&gt;" = assign / change.
    /// </summary>
    public Optional<string?> AppId { get; init; }

    /// <summary>PATCH semantics: null = no change.</summary>
    public bool? AllowDynamicRegistrationClients { get; init; }
}

public record OAuthScopeListDto
{
    public required List<OAuthScopeDto> Items { get; init; }
    public int TotalCount { get; init; }
}
