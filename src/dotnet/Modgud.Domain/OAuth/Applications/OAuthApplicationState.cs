using System.Text.Json.Serialization;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Domain.OAuth.Applications;

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
    /// <summary>
    /// n:m FK to <c>App.Id</c>. Empty = realm-wide / unassigned. The token
    /// pipeline (UserInfo + scope restriction) and the distribution API
    /// derive the calling client's app context from this list.
    /// </summary>
    public List<Guid> AppIds { get; set; } = [];
    public AccessTokenType AccessTokenType { get; set; } = AccessTokenType.Reference;

    /// <summary>
    /// Link to a ServiceAccount that owns this client's credentials. Set
    /// on a <c>client_credentials</c>-only client to make the SA the
    /// effective principal at the token endpoint (<c>sub = SA.Id</c>).
    /// Null on user-flow clients (<c>authorization_code</c>, etc.) —
    /// strict separation: one OAuth client = one auth mode.
    /// </summary>
    public Guid? LinkedServiceAccountId { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Transient — never persisted; used to surface fresh secrets to API responses.</summary>
    [JsonIgnore] public string? PendingClientSecret { get; set; }

    /// <summary>Transient — never persisted.</summary>
    [JsonIgnore] public string? PendingJsonWebKeySet { get; set; }
}
