using Marten.Schema;

namespace Modgud.Infrastructure.Installation;

/// <summary>
/// Deployment-wide installation marker. It lives in the Global Store because
/// no realm exists while the first installation challenge is issued.
/// </summary>
[DocumentAlias("installation_state")]
public sealed class InstallationState
{
    public const string SingletonId = "installation";

    public string Id { get; init; } = SingletonId;
    public bool IsCompleted { get; set; }
    public string? RealmSlug { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Short-lived, one-shot operator authorization for the first installation.
/// Only the SHA-256 hash is persisted; the plaintext token exists solely in
/// CLI output and the URL handed to the browser or CI.
/// </summary>
[DocumentAlias("installation_challenge")]
public sealed class InstallationChallenge
{
    public Guid Id { get; init; }
    public string TokenHash { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) =>
        UsedAt is null && RevokedAt is null && now < ExpiresAt;
}

public sealed record InstallationStatus(
    bool IsInitialized,
    bool HasRealms,
    string? RealmSlug,
    DateTimeOffset? CompletedAt);

public sealed record IssuedInstallationChallenge(
    Guid Id,
    string PlaintextToken,
    string InstallUrl,
    DateTimeOffset ExpiresAt);
