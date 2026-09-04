using System.Diagnostics.Metrics;
using Modgud.Domain.Realms;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>Rejection counter per policy and dimension (log-only rejections tagged
/// separately). Never carries the bucket value — no addresses, no mailboxes.</summary>
public static class RateLimitMetrics
{
    public const string MeterName = "Modgud.RateLimiting";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>(
        "modgud.auth.rate_limit.rejections",
        unit: "{request}",
        description: "Requests rejected (or, in log-only mode, that would have been rejected) by an auth rate-limit dimension.");

    private static readonly Counter<long> LoginThrottledCounter = Meter.CreateCounter<long>(
        "modgud.auth.login.throttled",
        unit: "{attempt}",
        description: "ADR 0008 — password login attempts refused (or, in log-only mode, that would have been) because a failure bucket was exhausted.");

    private static readonly Counter<long> LoginSprayCounter = Meter.CreateCounter<long>(
        "modgud.auth.login.spray_detected",
        unit: "{source}",
        description: "ADR 0008 — a source crossed the untrusted-failures-per-source signal threshold (once per window; never blocks).");

    public static void LoginThrottled(string bucket, bool logOnly) =>
        LoginThrottledCounter.Add(1,
            new KeyValuePair<string, object?>("bucket", bucket),
            new KeyValuePair<string, object?>("mode", logOnly ? "log-only" : "enforce"));

    public static void LoginSprayDetected() => LoginSprayCounter.Add(1);

    public static void Rejected(AuthRateLimitPolicy policy, RateLimitDimension dimension, bool logOnly) =>
        Rejections.Add(1,
            new KeyValuePair<string, object?>("policy", AuthRateLimitDefaults.PolicyName(policy)),
            new KeyValuePair<string, object?>("dimension", RateLimitEvaluator.DimensionName(dimension)),
            new KeyValuePair<string, object?>("mode", logOnly ? "log-only" : "enforce"));
}
