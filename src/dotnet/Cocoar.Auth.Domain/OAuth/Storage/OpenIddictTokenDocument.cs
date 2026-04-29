using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.OAuth.Storage;

/// <summary>
/// Document entity for OpenIddict tokens. NOT event-sourced — tokens are
/// security-sensitive and ephemeral. Stored as a plain document in each realm's
/// tenant DB.
/// </summary>
public class OpenIddictTokenDocument
{
    [JsonInclude] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonInclude] public string? ApplicationId { get; set; }
    [JsonInclude] public string? AuthorizationId { get; set; }
    [JsonInclude] public DateTimeOffset? CreationDate { get; set; }
    [JsonInclude] public DateTimeOffset? ExpirationDate { get; set; }
    [JsonInclude] public string? Payload { get; set; }
    [JsonInclude] public Dictionary<string, object?> Properties { get; set; } = new();
    [JsonInclude] public DateTimeOffset? RedemptionDate { get; set; }
    [JsonInclude] public string? ReferenceId { get; set; }
    [JsonInclude] public string? Status { get; set; }
    [JsonInclude] public string? Subject { get; set; }
    [JsonInclude] public string? Type { get; set; }
    [JsonInclude] public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();
}
