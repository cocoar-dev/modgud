using System.Text.Json;

namespace TimeToDo.Authentication.Domain.ExternalAuth.Events;

// ── IdP Config lifecycle events ──────────────────────────────────────

/// <summary>
/// Admin adds a new IdP configuration. Secret-bytes travel in this event because
/// it is the only point where the projection needs them to populate the
/// document; for rotations, see <see cref="IdpConfigSecretRotatedEvent"/>.
/// </summary>
public record IdpConfigAddedEvent(
    Guid Id,
    string Flavor,
    string DisplayName,
    bool Enabled,
    string ClientId,
    byte[]? ClientSecretEncrypted,
    List<string> Scopes,
    string UserUpdateScript,
    bool StoreRawClaims,
    int? RawClaimsRetentionDays,
    bool AutoCreateUsers,
    bool AllowLinking,
    bool TrustForEmailLink,
    List<string>? AllowedEmailDomains,
    string? IconName,
    string? ButtonColorHex,
    JsonDocument? FlavorData,
    DateTimeOffset CreatedAt);

/// <summary>
/// Admin updates an IdP configuration. Does NOT carry secret bytes — rotations
/// flow through <see cref="IdpConfigSecretRotatedEvent"/> to keep clean audit
/// separation.
/// </summary>
public record IdpConfigUpdatedEvent(
    Guid Id,
    string DisplayName,
    string ClientId,
    List<string> Scopes,
    string UserUpdateScript,
    bool StoreRawClaims,
    int? RawClaimsRetentionDays,
    bool AutoCreateUsers,
    bool AllowLinking,
    bool TrustForEmailLink,
    List<string>? AllowedEmailDomains,
    string? IconName,
    string? ButtonColorHex,
    JsonDocument? FlavorData,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Client-secret rotation. Encrypted bytes ride the event only because the
/// inline projection needs to persist them; the event record otherwise carries
/// no secret-leaking surface (no plaintext, no display form).
/// </summary>
public record IdpConfigSecretRotatedEvent(
    Guid Id,
    byte[] ClientSecretEncrypted,
    Guid? RotatedByUserId,
    DateTimeOffset RotatedAt);

public record IdpConfigEnabledEvent(Guid Id, DateTimeOffset At);
public record IdpConfigDisabledEvent(Guid Id, DateTimeOffset At);
public record IdpConfigDeletedEvent(Guid Id, DateTimeOffset At);
