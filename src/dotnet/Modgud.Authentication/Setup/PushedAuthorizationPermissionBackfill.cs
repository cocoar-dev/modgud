using Marten;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Setup;

/// <summary>
/// One-time, idempotent boot migration (2026-07): backfills the RFC-9126 PAR
/// endpoint permission <c>ept:pushed_authorization</c> onto every existing
/// OAuth client that predates it.
///
/// <para>Since Modgud advertises <c>/connect/par</c> in discovery, current
/// .NET OIDC clients call it by default (<c>PushedAuthorizationBehavior.UseIfAvailable</c>).
/// <c>OAuthAdminMapping.BuildClientPermissions()</c> grants the permission as a
/// baseline, but only to clients that are created or whose permissions are
/// recalculated (a grant/scope change, or an unrelated re-save). Clients
/// persisted before that change kept their old permission list and failed the
/// PAR challenge with <c>unauthorized_client</c> / OpenIddict <c>ID2183</c>
/// until an admin re-saved them. This closes that upgrade gap.</para>
///
/// <para>Same cold-start-walks-every-realm shape as
/// <see cref="LegacyAdminGroupRenameBootstrap"/>: re-enter <see cref="TenantContext"/>
/// per realm so the tenant-scoped <see cref="IDocumentSession"/> reads/writes the
/// correct database, and append directly to the event stream (no Wolverine bus,
/// so tenant DBs never need the durable-inbox tables). Naturally idempotent — a
/// repeat boot finds every client already carrying the permission and appends
/// nothing.</para>
///
/// <para>Only <see cref="OAuthApplicationAggregate.Permissions"/> is touched, and
/// only by <em>adding</em> the one missing entry. The PAR <em>requirement</em>
/// (<c>ft:par</c> / <c>RequirePushedAuthorizationRequests</c>) lives in the
/// separate <see cref="OAuthApplicationAggregate.Requirements"/> list and is left
/// exactly as-is — this grants "may use PAR", never "must use PAR".</para>
/// </summary>
public class PushedAuthorizationPermissionBackfill(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache,
    ILogger<PushedAuthorizationPermissionBackfill> logger) : IHostedService
{
    private const string ParPermission = OAuthPermissions.Endpoints.PushedAuthorization;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var realms = await realmCache.GetAllActiveAsync();
        var totalBackfilled = 0;

        foreach (var realm in realms)
        {
            try
            {
                using var _ = TenantContext.Enter(realm.Slug);
                using var scope = scopeFactory.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

                // The projection carries the authoritative permission set, so we
                // only aggregate + append for streams that actually lack it — a
                // repeat boot returns zero candidates and does no stream work.
                var candidates = await session.Query<OAuthApplicationState>()
                    .Where(x => !x.IsDeleted)
                    .ToListAsync(cancellationToken);

                var backfilled = 0;
                foreach (var state in candidates)
                {
                    if (state.Permissions.Contains(ParPermission)) continue;

                    var aggregate = await session.Events
                        .AggregateStreamAsync<OAuthApplicationAggregate>(state.Id, token: cancellationToken);
                    // Re-check on the authoritative aggregate; skip deleted streams.
                    if (aggregate is null || aggregate.IsDeleted) continue;
                    if (aggregate.Permissions.Contains(ParPermission)) continue;

                    // Add-only: every existing permission (grants, scopes, response
                    // types, other endpoints) is carried through unchanged.
                    var permissions = new List<string>(aggregate.Permissions) { ParPermission };
                    session.Events.Append(state.Id, aggregate.SetPermissions(permissions));
                    backfilled++;
                }

                if (backfilled > 0)
                {
                    await session.SaveChangesAsync(cancellationToken);
                    totalBackfilled += backfilled;
                    logger.LogInformation(
                        "Backfilled '{Perm}' on {Count} OAuth client(s) in realm {Realm}",
                        ParPermission, backfilled, realm.Slug);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "PAR permission backfill failed for realm {Realm} — it self-heals on the next boot",
                    realm.Slug);
            }
        }

        if (totalBackfilled > 0)
        {
            logger.LogInformation(
                "PushedAuthorizationPermissionBackfill added '{Perm}' to {Count} client(s) across {Realms} realm(s)",
                ParPermission, totalBackfilled, realms.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
