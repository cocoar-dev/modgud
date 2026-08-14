using JasperFx.Events.Daemon;
using Marten;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;

namespace Modgud.Authentication.Projections;

/// <summary>
/// Rebuilds all event-sourced <see cref="Principal"/> subtypes without deleting
/// directly stored <see cref="ServiceAccount"/> documents from their shared table.
/// </summary>
public static class PrincipalProjectionRebuilder
{
    /// <summary>
    /// Replays both projections in place, then deletes stale Person and Group
    /// discriminator rows that have no non-archived creation event. Neither
    /// projection may use Marten's default teardown because it truncates the root
    /// <c>mt_doc_principal</c> table.
    /// </summary>
    public static async Task RebuildAsync(
        IDocumentStore store,
        IProjectionDaemon daemon,
        string tenantId,
        TimeSpan timeout,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await daemon.RebuildProjectionAsync<PersonProjection>(timeout, ct);
        progress?.Invoke("OK PersonProjection (mt_doc_principal/person)");

        await daemon.RebuildProjectionAsync<GroupProjection>(timeout, ct);
        progress?.Invoke("OK GroupProjection (mt_doc_principal/group)");

        await using var session = store.LightweightSession(tenantId);
        // Replay happens before cleanup so live reads and inline writes never see
        // a deliberately emptied principal table. The creation-event aliases are
        // stable persisted contracts registered in MartenConfiguration and the
        // authorization slice. Archived streams must not keep a projected row.
        // ServiceAccount is excluded by discriminator and remains byte-for-byte
        // untouched because it has no event stream to replay.
        session.QueueSqlCommand(
            """
            delete from mt_doc_principal as principal
            where
                (principal.mt_doc_type = ? and not exists (
                    select 1
                    from mt_events as e
                    where e.stream_id = principal.id
                      and e.type in (?, ?)
                      and coalesce(e.is_archived, false) = false
                ))
                or
                (principal.mt_doc_type = ? and not exists (
                    select 1
                    from mt_events as e
                    where e.stream_id = principal.id
                      and e.type = ?
                      and coalesce(e.is_archived, false) = false
                ));
            """,
            "person",
            "user_created",
            "user_migrated",
            "group",
            "authorization_group_created");
        await session.SaveChangesAsync(ct);
    }
}
