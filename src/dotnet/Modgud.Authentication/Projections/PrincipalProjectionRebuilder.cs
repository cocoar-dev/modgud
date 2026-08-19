using JasperFx.Events.Daemon;
using Marten;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;

namespace Modgud.Authentication.Projections;

/// <summary>
/// Rebuilds all event-sourced <see cref="Principal"/> subtypes without deleting
/// legacy document-only <see cref="ServiceAccount"/> rows from their shared table.
/// </summary>
public static class PrincipalProjectionRebuilder
{
    /// <summary>
    /// Replays every Principal projection in place, then deletes stale Person,
    /// Group, and Position
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

        await daemon.RebuildProjectionAsync<PositionPrincipalProjection>(timeout, ct);
        progress?.Invoke("OK PositionPrincipalProjection (mt_doc_principal/position)");

        await daemon.RebuildProjectionAsync<ServiceAccountProjection>(timeout, ct);
        progress?.Invoke("OK ServiceAccountProjection (mt_doc_principal/service-account)");

        await using var session = store.LightweightSession(tenantId);
        // Replay happens before cleanup so live reads and inline writes never see
        // a deliberately emptied principal table. The creation-event aliases are
        // stable persisted contracts registered in MartenConfiguration and the
        // authorization slice. Archived streams must not keep a projected row.
        // Service-account cleanup is deliberately omitted: old installations
        // can still contain valid document-only rows without a creation event.
        // The teardown-free replay above updates every event-sourced account
        // while preserving those legacy snapshots until their first mutation
        // seeds a stream.
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
            "authorization_group_created",
            "position",
            "authorization_position_created");
        await session.SaveChangesAsync(ct);
    }
}
