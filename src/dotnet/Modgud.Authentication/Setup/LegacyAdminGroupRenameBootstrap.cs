using Marten;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Setup;

/// <summary>
/// One-time, idempotent boot migration: renames a realm's legacy
/// <see cref="AdminGroupNames.Legacy"/> ("Administratoren") group to
/// <see cref="AdminGroupNames.Current"/> ("Administrators") — the
/// English-naming pass (2026-07). Only <see cref="Group.Name"/> changes;
/// membership, roles, and every other field are carried over unchanged.
///
/// <para>
/// Cold-start-only, same pattern as <c>OidcSchemeBootstrap</c> /
/// <c>SamlSchemeBootstrap</c>: walks every active realm, re-entering
/// <see cref="TenantContext"/> per realm so the tenant-scoped
/// <see cref="IDocumentSession"/> reads/writes the correct database. Always
/// runs at boot (not gated behind a one-shot flag) — it is naturally
/// idempotent because the second run finds no group left named
/// <see cref="AdminGroupNames.Legacy"/>.
/// </para>
///
/// <para>
/// Deliberately NOT routed through a Wolverine command/bus: a tenant-targeted
/// command whose event has a durable-inbox forwarder writes
/// <c>wolverine_*</c> tables into the tenant DB, which can fail for tenants
/// that never needed that infrastructure. This mirrors
/// <see cref="RealmAdminBootstrapper"/> — a plain tenant-scoped
/// <see cref="IDocumentSession"/> with the event appended directly.
/// </para>
///
/// <para>
/// Edge case: a realm somehow carrying both a <see cref="AdminGroupNames.Legacy"/>
/// AND a <see cref="AdminGroupNames.Current"/> group (non-deleted) is left
/// alone — renaming would either collide or silently merge two groups an
/// operator may have deliberately kept distinct. Logged as a warning for
/// manual resolution instead.
/// </para>
/// </summary>
public class LegacyAdminGroupRenameBootstrap(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache,
    ILogger<LegacyAdminGroupRenameBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var realms = await realmCache.GetAllActiveAsync();
        var renamedCount = 0;

        foreach (var realm in realms)
        {
            try
            {
                // TenantContext.Enter sets the AsyncLocal that TenantedSessionFactory
                // reads when no HttpContext is present — without it the session
                // would query the system tenant for every realm.
                using var _ = TenantContext.Enter(realm.Slug);
                using var scope = scopeFactory.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

                var legacyGroup = await session.Query<Group>()
                    .Where(g => !g.IsDeleted && g.Name == AdminGroupNames.Legacy)
                    .FirstOrDefaultAsync(cancellationToken);

                if (legacyGroup is null) continue;

                var currentGroupExists = await session.Query<Group>()
                    .Where(g => !g.IsDeleted && g.Name == AdminGroupNames.Current)
                    .AnyAsync(cancellationToken);

                if (currentGroupExists)
                {
                    logger.LogWarning(
                        "Realm {Realm} has both a legacy '{Legacy}' group ({LegacyId}) and a " +
                        "'{Current}' group — leaving the legacy group untouched to avoid merging " +
                        "two distinct groups. Resolve manually.",
                        realm.Slug, AdminGroupNames.Legacy, legacyGroup.Id, AdminGroupNames.Current);
                    continue;
                }

                // Full-fidelity rename — every field copied through unchanged
                // except Name. Append-only; PrincipalProjection.Apply mutates
                // the existing doc, so we never re-Store it ourselves.
                session.Events.Append(legacyGroup.Id, new GroupUpdatedEvent(
                    legacyGroup.Id,
                    Name: AdminGroupNames.Current,
                    Description: legacyGroup.Description,
                    MemberIds: legacyGroup.MemberIds,
                    RoleIds: legacyGroup.RoleIds,
                    MembershipMode: legacyGroup.MembershipMode,
                    MembershipScript: legacyGroup.MembershipScript,
                    CompiledMembershipScript: legacyGroup.CompiledMembershipScript,
                    MembershipScriptDependencies: legacyGroup.MembershipScriptDependencies,
                    Email: legacyGroup.Email,
                    EmailMode: legacyGroup.EmailMode,
                    BoundTo: legacyGroup.BoundTo,
                    ExternallyDrivable: legacyGroup.ExternallyDrivable));

                await session.SaveChangesAsync(cancellationToken);
                renamedCount++;

                logger.LogInformation(
                    "Renamed legacy admin group '{Legacy}' to '{Current}' in realm {Realm}",
                    AdminGroupNames.Legacy, AdminGroupNames.Current, realm.Slug);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Legacy admin-group rename failed for realm {Realm} — it self-heals on the next boot",
                    realm.Slug);
            }
        }

        if (renamedCount > 0)
        {
            logger.LogInformation(
                "LegacyAdminGroupRenameBootstrap renamed {Count} legacy admin group(s) across {Realms} realm(s)",
                renamedCount, realms.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
