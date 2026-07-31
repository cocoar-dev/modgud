using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Marten;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Installation;

public interface IInstallationChallengeService
{
    Task<InstallationStatus> GetStatusAsync(CancellationToken ct = default);

    Task<ErrorOr<IssuedInstallationChallenge>> IssueAsync(
        string baseUrl,
        TimeSpan lifetime,
        CancellationToken ct = default);

    Task<ErrorOr<InstallationChallenge>> ValidateAsync(
        string plaintextToken,
        CancellationToken ct = default);

    Task<ErrorOr<Success>> CompleteAsync(
        string plaintextToken,
        string realmSlug,
        CancellationToken ct = default);
}

public sealed class InstallationChallengeService(
    IGlobalStore globalStore,
    TimeProvider clock,
    ISecurityAuditLog securityAudit) : IInstallationChallengeService
{
    public async Task<InstallationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var session = globalStore.QuerySession();
        var state = await session.LoadAsync<InstallationState>(InstallationState.SingletonId, ct);
        var realms = await session.Query<Realm>().Where(r => r.IsActive).ToListAsync(ct);

        // Existing deployments predate InstallationState. Any active realm is
        // therefore authoritative evidence that installation already happened.
        var firstRealm = realms.OrderBy(r => r.CreatedAt).FirstOrDefault();
        return new InstallationStatus(
            state?.IsCompleted == true || firstRealm is not null,
            firstRealm is not null,
            state?.RealmSlug ?? firstRealm?.Slug,
            state?.CompletedAt);
    }

    public async Task<ErrorOr<IssuedInstallationChallenge>> IssueAsync(
        string baseUrl,
        TimeSpan lifetime,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return Error.Validation(
                "Installation.InvalidBaseUrl",
                "Base URL must be an absolute HTTP(S) URL without query or fragment.");
        }

        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24))
        {
            return Error.Validation(
                "Installation.InvalidLifetime",
                "Challenge lifetime must be greater than zero and at most 24 hours.");
        }

        await using var session = globalStore.LightweightSession();
        if (await session.Query<Realm>().AnyAsync(ct))
        {
            return Error.Conflict(
                "Installation.AlreadyInitialized",
                "At least one realm already exists; first installation is no longer available.");
        }

        var state = await session.LoadAsync<InstallationState>(InstallationState.SingletonId, ct);
        if (state?.IsCompleted == true)
        {
            return Error.Conflict(
                "Installation.AlreadyInitialized",
                "The deployment has already been initialized.");
        }

        var now = clock.GetUtcNow();
        var openChallenges = await session.Query<InstallationChallenge>()
            .Where(c => c.UsedAt == null && c.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var open in openChallenges)
        {
            open.RevokedAt = now;
            session.Store(open);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = new InstallationChallenge
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(token),
            BaseUrl = baseUrl.TrimEnd('/'),
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
        };
        session.Store(challenge);
        securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.InstallationChallengeIssued,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "issue-install-link",
            Domain = uri.Host,
            EffectiveAt = challenge.ExpiresAt,
        });
        await session.SaveChangesAsync(ct);

        return new IssuedInstallationChallenge(
            challenge.Id,
            token,
            $"{challenge.BaseUrl}/install?token={Uri.EscapeDataString(token)}",
            challenge.ExpiresAt);
    }

    public async Task<ErrorOr<InstallationChallenge>> ValidateAsync(
        string plaintextToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            return Error.Validation("Installation.TokenRequired", "Installation token is required.");

        await using var session = globalStore.QuerySession();
        var challenge = await session.Query<InstallationChallenge>()
            .FirstOrDefaultAsync(c => c.TokenHash == Hash(plaintextToken), ct);

        if (challenge is null)
            return Error.Validation("Installation.TokenInvalid", "Installation token is invalid.");
        if (challenge.UsedAt is not null)
            return Error.Validation("Installation.TokenUsed", "Installation token has already been used.");
        if (challenge.RevokedAt is not null)
            return Error.Validation("Installation.TokenRevoked", "Installation token has been revoked.");
        if (clock.GetUtcNow() >= challenge.ExpiresAt)
            return Error.Validation("Installation.TokenExpired", "Installation token has expired.");

        return challenge;
    }

    public async Task<ErrorOr<Success>> CompleteAsync(
        string plaintextToken,
        string realmSlug,
        CancellationToken ct = default)
    {
        var tokenHash = Hash(plaintextToken);
        await using var session = globalStore.LightweightSession();
        var challenge = await session.Query<InstallationChallenge>()
            .FirstOrDefaultAsync(c => c.TokenHash == tokenHash, ct);
        var now = clock.GetUtcNow();

        if (challenge is null || !challenge.IsUsable(now))
            return Error.Validation("Installation.TokenInvalid", "Installation token is invalid or no longer usable.");

        challenge.UsedAt = now;
        session.Store(challenge);
        session.Store(new InstallationState
        {
            IsCompleted = true,
            RealmSlug = realmSlug,
            CompletedAt = now,
            UpdatedAt = now,
        });
        securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.InstallationCompleted,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "complete-installation",
            TargetRealmSlug = realmSlug,
        });
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
