using System.Text.Json.Serialization;

namespace Modgud.Domain.OAuth.Applications;

/// <summary>
/// Security-sensitive document for an OAuth application. NOT event-sourced —
/// password hashes / JWKs must never appear in the event history. Same Id as
/// the matching <see cref="OAuthApplicationAggregate"/>.
/// </summary>
public class OAuthApplicationSecurityData
{
    [JsonInclude] public Guid Id { get; set; }
    [JsonInclude] public string? ClientSecret { get; set; }
    [JsonInclude] public string? JsonWebKeySet { get; set; }
    [JsonInclude] public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();

    public static OAuthApplicationSecurityData Create(Guid applicationId) =>
        new() { Id = applicationId, ConcurrencyToken = Guid.NewGuid().ToString() };

    public void UpdateConcurrencyToken() => ConcurrencyToken = Guid.NewGuid().ToString();
}
