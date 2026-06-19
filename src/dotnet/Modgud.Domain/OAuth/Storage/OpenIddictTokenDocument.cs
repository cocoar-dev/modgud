using System.Text.Json.Serialization;

namespace Modgud.Domain.OAuth.Storage;

/// <summary>
/// Document entity for OpenIddict tokens. NOT event-sourced — tokens are
/// security-sensitive and ephemeral. Stored as a plain document in each realm's
/// tenant DB.
///
/// <para><see cref="ConcurrencyToken"/> is the optimistic-concurrency guard for
/// the refresh-token redeem (Audit #22). OpenIddict spreads the redeem across two
/// sessions (the manager loads the token via <c>FindBy…</c>, then the store flips
/// it to Redeemed in a fresh session via <c>UpdateAsync</c>) and preserves this
/// field untouched across that boundary, so the store can compare the caller's
/// loaded token against the live row and reject a stale write. See
/// <c>MartenTokenStore.UpdateAsync</c>.</para>
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
