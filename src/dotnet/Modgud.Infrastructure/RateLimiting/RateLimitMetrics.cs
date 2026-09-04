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

    public static void Rejected(AuthRateLimitPolicy policy, RateLimitDimension dimension, bool logOnly) =>
        Rejections.Add(1,
            new KeyValuePair<string, object?>("policy", AuthRateLimitDefaults.PolicyName(policy)),
            new KeyValuePair<string, object?>("dimension", RateLimitEvaluator.DimensionName(dimension)),
            new KeyValuePair<string, object?>("mode", logOnly ? "log-only" : "enforce"));
}
