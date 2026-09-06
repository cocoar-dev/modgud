using Microsoft.Extensions.Logging;
using Modgud.Domain.Realms;

namespace Modgud.Infrastructure.RateLimiting;

public enum RateLimitOutcome
{
    Allow,
    /// <summary>A loud dimension rejected: the caller gets 429 with Retry-After.</summary>
    Reject,
}

public sealed record RateLimitDecision(
    RateLimitOutcome Outcome,
    AuthRateLimitPolicy Policy,
    RateLimitDimension? Dimension,
    int RetryAfterSeconds,
    /// <summary>The realm runs the subsystem in log-only mode: nothing is ever rejected,
    /// but would-be rejections are logged and counted.</summary>
    bool LogOnly)
{
    public static RateLimitDecision Allowed(AuthRateLimitPolicy policy, bool logOnly) => new(RateLimitOutcome.Allow, policy, null, 0, logOnly);
}

/// <summary>
/// ADR 0019 — evaluates a policy's dimensions against the caller. Roles are fixed:
/// <c>target</c> and <c>app</c> are the defence (mailbox and mail budget), <c>client</c>
/// bounds one integration, <c>source</c> is a coarse anomaly brake sized for NATs.
/// A trusted forwarder shifts only the source dimensions.
/// </summary>
public interface IRateLimitEvaluator
{
    /// <summary>Loud dimensions, evaluated cheapest-rejection-first; the first rejection
    /// wins and later dimensions are not charged.</summary>
    Task<RateLimitDecision> EvaluateAsync(
        AuthRateLimitPolicy policy,
        AuthCallerContext caller,
        AuthRateLimitSettings? settings,
        string? target,
        string? clientKey,
        CancellationToken ct = default);

    /// <summary>The silent <see cref="RateLimitDimension.SourceRegistration"/> ceiling:
    /// how often one source may enter the registration pipeline (unknown address).
    /// Never surfaces as 429 — the caller answers uniformly and simply sends nothing.</summary>
    Task<bool> AllowRegistrationEntryAsync(
        AuthRateLimitPolicy policy,
        AuthCallerContext caller,
        AuthRateLimitSettings? settings,
        CancellationToken ct = default);
}

public sealed class RateLimitEvaluator(
    IRateLimitStore store,
    ILogger<RateLimitEvaluator> logger,
    TimeProvider? clock = null) : IRateLimitEvaluator
{
    private static readonly RateLimitDimension[] LoudOrder =
        [RateLimitDimension.Source, RateLimitDimension.Target, RateLimitDimension.Client, RateLimitDimension.App];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<RateLimitDecision> EvaluateAsync(
        AuthRateLimitPolicy policy,
        AuthCallerContext caller,
        AuthRateLimitSettings? settings,
        string? target,
        string? clientKey,
        CancellationToken ct = default)
    {
        var logOnly = AuthRateLimitSettings.EffectiveMode(settings) == RateLimitEnforcementMode.LogOnly;
        var now = _clock.GetUtcNow();
        var scope = new RateLimitScope(caller.RealmSlug);

        foreach (var dimension in LoudOrder)
        {
            var rule = AuthRateLimitSettings.Effective(settings, policy, dimension);
            if (rule is null || !rule.Enabled) continue;

            var key = KeyFor(policy, dimension, caller, target, clientKey);
            if (key is null) continue;

            var hit = await store.HitAsync(scope, key, rule, now, ct);
            if (hit.Allowed) continue;

            RateLimitMetrics.Rejected(policy, dimension, logOnly);
            if (logOnly)
            {
                logger.LogWarning(
                    "Rate limit (log-only): {Policy}/{Dimension} would reject (realm={Realm}, retry-after={RetryAfter}s)",
                    AuthRateLimitDefaults.PolicyName(policy), dimension, caller.RealmSlug ?? "-", hit.RetryAfterSeconds);
                continue;
            }

            logger.LogInformation(
                "Rate limit: {Policy}/{Dimension} rejected (realm={Realm}, retry-after={RetryAfter}s)",
                AuthRateLimitDefaults.PolicyName(policy), dimension, caller.RealmSlug ?? "-", hit.RetryAfterSeconds);
            return new RateLimitDecision(RateLimitOutcome.Reject, policy, dimension, hit.RetryAfterSeconds, false);
        }

        return RateLimitDecision.Allowed(policy, logOnly);
    }

    public async Task<bool> AllowRegistrationEntryAsync(
        AuthRateLimitPolicy policy,
        AuthCallerContext caller,
        AuthRateLimitSettings? settings,
        CancellationToken ct = default)
    {
        var rule = AuthRateLimitSettings.Effective(settings, policy, RateLimitDimension.SourceRegistration);
        if (rule is null || !rule.Enabled || caller.SourceAllowlisted) return true;

        var key = KeyFor(policy, RateLimitDimension.SourceRegistration, caller, null, null);
        if (key is null) return true;

        var hit = await store.HitAsync(new RateLimitScope(caller.RealmSlug), key, rule, _clock.GetUtcNow(), ct);
        if (hit.Allowed) return true;

        var logOnly = AuthRateLimitSettings.EffectiveMode(settings) == RateLimitEnforcementMode.LogOnly;
        RateLimitMetrics.Rejected(policy, RateLimitDimension.SourceRegistration, logOnly);
        logger.LogWarning(
            "Rate limit{Mode}: {Policy}/source-registration — address spraying from one source (realm={Realm})",
            logOnly ? " (log-only)" : "", AuthRateLimitDefaults.PolicyName(policy), caller.RealmSlug ?? "-");
        return logOnly;
    }

    /// <summary>The bucket key, or null when the dimension does not apply to this call.</summary>
    internal static string? KeyFor(AuthRateLimitPolicy policy, RateLimitDimension dimension, AuthCallerContext caller, string? target, string? clientKey)
    {
        var name = AuthRateLimitDefaults.PolicyName(policy);
        var realm = caller.RealmSlug ?? "-";
        var value = dimension switch
        {
            RateLimitDimension.Source or RateLimitDimension.SourceRegistration =>
                caller.SourceAllowlisted ? null : caller.SourceKey,
            RateLimitDimension.Target => string.IsNullOrWhiteSpace(target) ? null : target.Trim().ToUpperInvariant(),
            RateLimitDimension.Client => caller.ClientId ?? (string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim()),
            RateLimitDimension.App => caller.ApplicationId?.ToString("N") ?? "realm",
            _ => null,
        };
        return value is null ? null : $"{name}|{DimensionName(dimension)}|{realm}|{value}";
    }

    public static string DimensionName(RateLimitDimension dimension) => dimension switch
    {
        RateLimitDimension.Source => "source",
        RateLimitDimension.SourceRegistration => "source-registration",
        RateLimitDimension.Target => "target",
        RateLimitDimension.Client => "client",
        RateLimitDimension.App => "app",
        _ => dimension.ToString().ToLowerInvariant(),
    };
}
