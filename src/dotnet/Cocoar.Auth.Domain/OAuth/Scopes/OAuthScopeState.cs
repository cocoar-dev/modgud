namespace Cocoar.Auth.Domain.OAuth.Scopes;

/// <summary>
/// Inline projection target document for OAuth scopes. Kept in Domain so the
/// Application service can query it. Projection class lives in Infrastructure.
/// </summary>
public class OAuthScopeState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string> Resources { get; set; } = new();
    public Dictionary<string, string> DisplayNames { get; set; } = new();
    public Dictionary<string, string> Descriptions { get; set; } = new();
    public Dictionary<string, object?> Properties { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool Required { get; set; }
    public bool Emphasize { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> UserClaims { get; set; } = new();
    public bool IsDeleted { get; set; }
}
