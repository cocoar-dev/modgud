using System.Text.Json;

namespace Modgud.Authentication.Domain.LoginProviders.Events;

// ── Login provider lifecycle events ──────────────────────────────────

/// <summary>
/// Admin (or the realm seeder) adds a new login provider. <see cref="Type"/> is
/// part of creation and immutable thereafter — there is no separate "type
/// changed" event because changing a provider's type is essentially deleting
/// and recreating. Secret bytes ride this event because it is the only point
/// where the inline projection needs them to populate the document; for
/// rotations, see <see cref="LoginProviderSecretRotatedEvent"/>.
/// </summary>
public record LoginProviderAddedEvent(
    Guid Id,
    LoginProviderType Type,
    string Flavor,
    string DisplayName,
    string? Description,
    bool IsBuiltIn,
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
/// Admin updates a login provider. Does NOT carry secret bytes — rotations
/// flow through <see cref="LoginProviderSecretRotatedEvent"/> to keep clean
/// audit separation. <c>Type</c> + <c>Flavor</c> + <c>IsBuiltIn</c> are
/// immutable and not in this event.
/// </summary>
public record LoginProviderUpdatedEvent(
    Guid Id,
    string DisplayName,
    string? Description,
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
public record LoginProviderSecretRotatedEvent(
    Guid Id,
    byte[] ClientSecretEncrypted,
    Guid? RotatedByUserId,
    DateTimeOffset RotatedAt);

public record LoginProviderEnabledEvent(Guid Id, DateTimeOffset At);
public record LoginProviderDisabledEvent(Guid Id, DateTimeOffset At);
public record LoginProviderDeletedEvent(Guid Id, DateTimeOffset At);
