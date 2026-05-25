using System.Text.Json.Serialization;

namespace Modgud.Domain.OAuth.Storage;

/// <summary>
/// Document entity for OpenIddict authorizations. NOT event-sourced — authorizations
/// are security-sensitive and ephemeral; we store them as plain documents in each
/// realm's tenant DB. Mirrors the legacy backend so realm DBs reused from Legacy
/// keep working without a data migration.
/// </summary>
public class OpenIddictAuthorizationDocument
{
    [JsonInclude] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonInclude] public string? ApplicationId { get; set; }
    [JsonInclude] public DateTimeOffset? CreationDate { get; set; }
    [JsonInclude] public Dictionary<string, object?> Properties { get; set; } = new();
    [JsonInclude] public HashSet<string> Scopes { get; set; } = new();
    [JsonInclude] public string? Status { get; set; }
    [JsonInclude] public string? Subject { get; set; }
    [JsonInclude] public string? Type { get; set; }
    [JsonInclude] public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();
}
