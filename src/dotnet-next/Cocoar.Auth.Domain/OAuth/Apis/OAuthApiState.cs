namespace Cocoar.Auth.Domain.OAuth.Apis;

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
    public bool IsDeleted { get; set; }
}
