using System.Text.Json.Serialization;
using Cocoar.Auth.Domain.OAuth.Common;

namespace Cocoar.Auth.Domain.OAuth.Applications;

/// <summary>
/// Inline projection target document for OAuth applications. Lives in Domain
/// (rather than Infrastructure) so the Application service can query it without
/// taking an Infrastructure dependency. The matching projection class
/// (<c>OAuthApplicationStateProjection</c>) lives next to other Marten projections
/// in Infrastructure.
/// </summary>
public class OAuthApplicationState
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ClientType { get; set; }
    public string? ConsentType { get; set; }
    public string? ApplicationType { get; set; }
    public List<string> RedirectUris { get; set; } = new();
    public List<string> PostLogoutRedirectUris { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public List<string> Requirements { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new();
    public Dictionary<string, string> DisplayNames { get; set; } = new();
    public Dictionary<string, object?> Properties { get; set; } = new();
    public AccessTokenType AccessTokenType { get; set; } = AccessTokenType.Reference;
    public bool IsDeleted { get; set; }

    /// <summary>Transient — never persisted; used to surface fresh secrets to API responses.</summary>
    [JsonIgnore] public string? PendingClientSecret { get; set; }

    /// <summary>Transient — never persisted.</summary>
    [JsonIgnore] public string? PendingJsonWebKeySet { get; set; }
}
