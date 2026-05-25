namespace Modgud.Domain.OAuth.Apis;

/// <summary>
/// Inline projection target document for OAuth APIs. Kept in Domain so the
/// Application service can query it. Projection class lives in Infrastructure.
/// </summary>
public class OAuthApiState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Scopes { get; set; } = new();
    public List<string> UserClaims { get; set; } = new();
    public Dictionary<string, object?> Properties { get; set; } = new();
    /// <summary>FK to <c>App.Id</c>. Null = unassigned.</summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// Subset of the linked <c>App.Permissions</c> catalog this resource
    /// server gates on. Each entry is the <c>AppPermission.Id</c> of an
    /// entry in the linked App's catalog. Stable across resource/action
    /// renames. Empty list means the RS doesn't gate on anything (only
    /// authentication, no per-permission authz) — typical for a fresh RS
    /// before the operator has picked a subset.
    /// </summary>
    public List<Guid> PermissionIds { get; set; } = new();

    public bool IsDeleted { get; set; }
}
