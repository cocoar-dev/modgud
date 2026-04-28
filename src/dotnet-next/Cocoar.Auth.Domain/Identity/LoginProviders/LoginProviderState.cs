namespace Cocoar.Auth.Domain.Identity.LoginProviders;

/// <summary>
/// Inline projection target document for login providers. Kept in Domain so the
/// Application service can query it. Projection class lives in Infrastructure.
/// </summary>
public class LoginProviderState
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public LoginProviderType Type { get; set; }
    public Dictionary<string, string> Configuration { get; set; } = new();
    public bool IsBuiltIn { get; set; }
    public bool IsDeleted { get; set; }
}
