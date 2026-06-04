using Marten;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Daily hard-prune of the streamless security/ops audit store
/// (<see cref="SecurityAuditEntry"/>). Replaces the legacy
/// <c>AuthLogPersistenceService</c> cleanup loop with a Quartz job admins can see,
/// re-cron, and trigger from /admin/jobs.
///
/// <para>The short, FIXED retention window <b>is</b> the GDPR proportionality control
/// for this store: it holds personal data about unidentified actors (attempted
/// identifiers, IPs under CJEU <i>Breyer</i>) processed under Art. 6(1)(f) legitimate
/// interest, with no per-subject erase path — so a genuine hard delete on a tight
/// window keeps the processing proportionate. Deliberately NOT per-realm configurable
/// (unlike the per-realm GDPR-audit <i>visibility window</i>, which is a view bound,
/// not a deletion). See <c>dev-docs/future-features/logging-audit-redesign.md</c> §A.6
/// + the Legitimate-Interest Assessment.</para>
///
/// <para>The store is a single cross-realm doc set in the system DB, so this is one
/// indexed delete — no per-realm iteration.</para>
/// </summary>
[DisallowConcurrentExecution]
public class SecurityAuditPruneJob(IServiceScopeFactory scopeFactory) : IJob
{
    public const string Key = "security-audit-prune";
    public const string Name = "Security Audit Prune";
    public const string Description =
        "Hard-deletes streamless security/ops audit entries older than the fixed " +
        "short retention window (7 days). This retention is the GDPR proportionality " +
        "control for the legitimate-interest data the store holds; deliberately fixed, " +
        "not per-realm configurable.";

    /// <summary>Fixed short hard-retention for the legitimate-interest streamless store.
    /// (The per-realm GDPR-audit visibility window is a separate, configurable concept.)</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>02:00 UTC daily.</summary>
    public const string DefaultCron = "0 0 2 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession(TenantConstants.SystemTenantId);

        var cutoff = DateTimeOffset.UtcNow - Retention;
        var doomed = await session.Query<SecurityAuditEntry>()
            .CountAsync(x => x.Timestamp < cutoff, ct);

        session.DeleteWhere<SecurityAuditEntry>(x => x.Timestamp < cutoff);
        await session.SaveChangesAsync(ct);

        context.Result = doomed == 0
            ? "No entries to prune"
            : $"Pruned {doomed} security-audit entr(ies) older than {Retention.TotalDays:0} day(s)";
    }
}
