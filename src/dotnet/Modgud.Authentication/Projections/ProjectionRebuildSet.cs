using JasperFx.Events.Daemon;
using Marten;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Identity.ExternalAuth;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authorization.Projections;
using Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;
using Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

namespace Modgud.Authentication.Projections;

/// <summary>
/// B4-02 — the one set the admin rebuild endpoint and <c>recover rebuild-projections</c>
/// replay, so both rebuild every registered projection and report exactly that set.
///
/// <para>Every projection here is a pure projection of its event stream: its documents
/// are written only by the projection (streams are started for every create, also by
/// the realm seeders), so a replay restores them. Two need care: the Principal subtypes
/// share one table that also holds legacy document-only service accounts
/// (<see cref="PrincipalProjectionRebuilder"/>), and the audit view replays archived
/// events on purpose (<c>IncludeArchivedEvents</c>), so erased users' de-identified
/// rows survive. A projection registered later must be added here, or the rebuild
/// leaves it out and says so.</para>
/// </summary>
public static class ProjectionRebuildSet
{
    /// <summary>Replays the set for one tenant database and returns the projection names,
    /// in order. <paramref name="progress"/> gets one line per projection.</summary>
    public static async Task<IReadOnlyList<string>> RebuildAsync(
        IDocumentStore store,
        IProjectionDaemon daemon,
        string tenantId,
        TimeSpan timeout,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var rebuilt = new List<string>();

        await daemon.RebuildProjectionAsync("ViewProjections", timeout, ct);
        Done("ViewProjections", "UserView, InboxItem");

        await PrincipalProjectionRebuilder.RebuildAsync(store, daemon, tenantId, timeout, progress, ct);
        rebuilt.AddRange(["PersonProjection", "GroupProjection", "PositionPrincipalProjection", "ServiceAccountProjection"]);

        await One<PermissionRoleProjection>("mt_doc_permissionrole");
        await One<AppProjection>("mt_doc_app");
        await One<OAuthApplicationStateProjection>("OAuth clients");
        await One<OAuthScopeStateProjection>("OAuth scopes");
        await One<OAuthApiStateProjection>("OAuth APIs");
        await One<LoginProviderProjection>("login providers");
        await One<ExternalIdentityLinkProjection>("external identity links");
        await One<PositionGrantProjection>("position grants");
        await One<TerminalEnrollmentProjection>("terminal enrollments");
        await One<StaffingSessionProjection>("staffing sessions");
        await One<AuthAuditViewProjection>("audit view, archived events included");
        return rebuilt;

        async Task One<TProjection>(string what)
        {
            await daemon.RebuildProjectionAsync<TProjection>(timeout, ct);
            Done(typeof(TProjection).Name, what);
        }

        void Done(string name, string what)
        {
            rebuilt.Add(name);
            progress?.Invoke($"OK {name} ({what})");
        }
    }
}
