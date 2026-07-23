using Marten;
using Modgud.Authentication.RealmSettings;
using Modgud.Infrastructure.Audit;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Realm-owned hard-prune. Quartz creates one instance per realm; it reads the
/// owning realm's policy and deletes only that physical database's events.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SecurityAuditPruneJob(
    IDocumentSession session,
    IRealmSettingsService realmSettings) : IJob
{
    public const string Key = "security-audit-prune";
    public const string Name = "Security Audit Prune";
    public const string Description =
        "Hard-deletes this realm's structured security events after its configured retention period.";
    public const string DefaultCron = "0 0 2 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var settings = await realmSettings.LoadAsync(ct);
        var retentionDays = settings.Audit?.SecurityRetentionDays ?? 7;
        if (retentionDays is < 1 or > 365)
            throw new JobExecutionException(
                $"SecurityRetentionDays must be between 1 and 365, got {retentionDays}.");

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var doomed = await session.Query<RealmSecurityAuditEvent>()
            .CountAsync(x => x.Timestamp < cutoff, ct);
        session.DeleteWhere<RealmSecurityAuditEvent>(x => x.Timestamp < cutoff);
        await session.SaveChangesAsync(ct);

        context.Result = doomed == 0
            ? "No entries to prune"
            : $"Pruned {doomed} realm security event(s) older than {retentionDays} day(s)";
    }
}
