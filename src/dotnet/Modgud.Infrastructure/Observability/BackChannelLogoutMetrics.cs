using System.Diagnostics;
using System.Diagnostics.Metrics;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Infrastructure.Observability;

/// <summary>ADR 0021 — delivery counter and latency of logout-token POSTs. Tags: realm,
/// client (bounded: registered clients), outcome. Registered through
/// <see cref="ModgudMeters.Name"/>.</summary>
public static class BackChannelLogoutMetrics
{
    private static readonly Counter<long> Deliveries = ModgudMeters.Meter.CreateCounter<long>(
        "modgud.auth.backchannel_logout.deliveries",
        unit: "{attempt}",
        description: "Back-channel logout POST attempts by outcome (delivered / failed).");

    private static readonly Histogram<double> Duration = ModgudMeters.Meter.CreateHistogram<double>(
        "modgud.auth.backchannel_logout.duration",
        unit: "s",
        description: "Wall time of one back-channel logout POST attempt.");

    public static void Delivery(string clientId, string outcome, TimeSpan elapsed, string? realm = null)
    {
        var tags = new TagList
        {
            { "realm", realm ?? TenantContext.CurrentOrNull ?? "system" },
            { "client", clientId },
            { "outcome", outcome },
        };
        Deliveries.Add(1, tags);
        Duration.Record(elapsed.TotalSeconds, tags);
    }
}
