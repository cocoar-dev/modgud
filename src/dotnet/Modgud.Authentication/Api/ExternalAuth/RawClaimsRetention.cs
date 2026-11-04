using Marten;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// EP-03 — the contract behind <see cref="LoginProvider.RawClaimsRetentionDays"/>: raw IdP
/// claims captured before <c>now − days</c> are expired. Readers withhold them at once;
/// <see cref="RawClaimsRetentionSweeper"/> removes them from storage. <c>null</c> keeps
/// them as long as the link exists; a value below one day expires every capture.
/// </summary>
public static class RawClaimsRetention
{
    public static DateTimeOffset? CutoffFor(int? retentionDays, DateTimeOffset now)
        => retentionDays is { } days ? now.AddDays(-Math.Max(0, days)) : null;

    public static bool IsExpired(int? retentionDays, DateTimeOffset capturedAt, DateTimeOffset now)
        => CutoffFor(retentionDays, now) is { } cutoff && capturedAt < cutoff;
}

public sealed record RawClaimsSweepResult(int Links, int Events);

/// <summary>
/// EP-03 — removes expired raw claims in the current realm, for every provider with a
/// retention: the link's last snapshot (through <see cref="ExternalIdentityRawClaimsExpiredEvent"/>,
/// so a projection rebuild agrees) and the <c>RawClaims</c> field of every expired login
/// event in the provider's link streams — unlinked ones included, since their streams stay.
/// Nothing else in those events changes: email, display name and script output remain.
/// </summary>
public sealed class RawClaimsRetentionSweeper(IDocumentSession session, TimeProvider clock)
{
    // Stable event-type aliases (MartenStoreOptionsExtensions).
    private const string LinkedType = "external_identity_linked";
    private const string RecordedType = "external_identity_script_recorded";

    public async Task<RawClaimsSweepResult> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var providers = await session.Query<LoginProvider>()
            .Where(p => p.RawClaimsRetentionDays != null)
            .ToListAsync(ct);
        var schema = session.DocumentStore.Options.Events.DatabaseSchemaName;
        // A cleared snapshot is stored as JSON null (and reads back as a JsonDocument
        // with a Null root), not as a missing field: the selection checks the JSON type
        // so a second run reliably finds nothing.
        var linkTable = session.DocumentStore.Options.FindOrResolveDocumentType(typeof(ExternalIdentityLink)).TableName.QualifiedName;
        var expiredLinks = $$"""
            select d.id from {{linkTable}} as d
            where (d.data ->> 'LoginProviderId')::uuid = ?
              and coalesce(jsonb_typeof(d.data -> 'LastRawClaims'), 'null') <> 'null'
              and (d.data ->> 'LastCapturedAt')::timestamptz < ?
            """;
        var expiredEvents = $$"""
            from "{{schema}}".mt_events as e
            where e.type = ?
              and coalesce(jsonb_typeof(e.data -> 'RawClaims'), 'null') <> 'null'
              and (e.data ->> 'CapturedAt')::timestamptz < ?
              and e.stream_id in (
                  select l.stream_id from "{{schema}}".mt_events as l
                  where l.type = ? and (l.data ->> 'LoginProviderId')::uuid = ?)
            """;

        int links = 0, events = 0;
        foreach (var provider in providers)
        {
            var cutoff = RawClaimsRetention.CutoffFor(provider.RawClaimsRetentionDays, now)!.Value;

            var expired = await session.QueryAsync<Guid>(expiredLinks, ct, provider.Id, cutoff);
            foreach (var linkId in expired)
                session.Events.Append(linkId, new ExternalIdentityRawClaimsExpiredEvent(linkId, cutoff, now));
            links += expired.Count;

            var parameters = new object[] { RecordedType, cutoff, LinkedType, provider.Id };
            events += (await session.QueryAsync<int>($"select count(*)::int {expiredEvents}", ct, parameters)).Single();
            session.QueueSqlCommand(
                $$"""update "{{schema}}".mt_events as t set data = jsonb_set(t.data, '{RawClaims}', 'null'::jsonb) where t.seq_id in (select e.seq_id {{expiredEvents}})""",
                parameters);
        }

        await session.SaveChangesAsync(ct);
        return new RawClaimsSweepResult(links, events);
    }
}
