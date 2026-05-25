using System.Text.Json.Serialization;

namespace Modgud.Domain.OAuth.Apis;

/// <summary>Single API secret entry. <see cref="HashedValue"/> is BCrypt; never plaintext.</summary>
public class ApiSecretEntry
{
    [JsonInclude] public Guid SecretId { get; set; } = Guid.NewGuid();
    [JsonInclude] public string Type { get; set; } = "SharedSecret";
    [JsonInclude] public string HashedValue { get; set; } = string.Empty;
    [JsonInclude] public string? Description { get; set; }
    [JsonInclude] public DateTimeOffset? Expiration { get; set; }
    [JsonInclude] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Security-sensitive document for an OAuth API. NOT event-sourced. Same Id as
/// the matching <see cref="OAuthApiAggregate"/>.
/// </summary>
public class OAuthApiSecurityData
{
    [JsonInclude] public Guid Id { get; set; }
    [JsonInclude] public string? ApiSecret { get; set; } // legacy single-secret field for back-compat
    [JsonInclude] public List<ApiSecretEntry> Secrets { get; set; } = new();
    [JsonInclude] public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();

    public static OAuthApiSecurityData Create(Guid apiId) =>
        new() { Id = apiId, ConcurrencyToken = Guid.NewGuid().ToString() };

    public void UpdateConcurrencyToken() => ConcurrencyToken = Guid.NewGuid().ToString();
}
