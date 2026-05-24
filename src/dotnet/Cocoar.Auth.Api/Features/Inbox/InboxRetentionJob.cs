using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Cocoar.Auth.Application.Inbox;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;

namespace Cocoar.Auth.Api.Features.Inbox;

/// <summary>
/// Quartz wrapper around <see cref="IInboxRetentionService"/>. Runs daily
/// (default 03:00 UTC) and applies the per-kind retention policy stored in
/// <see cref="InboxRetentionSettings"/> for every active realm — each tenant
/// has its own retention settings doc in its own DB.
///
/// The job itself is intentionally dumb — no parameter schema, no per-run
/// config. Admins configure retention under <c>/admin/inbox-settings</c>,
/// and this job just orchestrates "when".
/// </summary>
[DisallowConcurrentExecution]
public class InboxRetentionJob(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache) : IJob
{
    public const string Key = "inbox-retention";
    public const string Name = "Inbox Retention";
    public const string Description =
        "Applies the inbox retention policy (configured under /admin/inbox-settings) " +
        "across every active realm.";
    /// <summary>03:00 UTC every day — before the other two retention jobs.</summary>
    public const string DefaultCron = "0 0 3 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var realms = await realmCache.GetAllActiveAsync();

        int totalAffected = 0;
        var breakdown = new Dictionary<string, int>();
        int tenantsProcessed = 0;

        foreach (var realm in realms)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var _ = TenantContext.Enter(realm.Slug);

                var retention = scope.ServiceProvider.GetRequiredService<IInboxRetentionService>();
                var result = await retention.ExecuteAsync(ct);

                totalAffected += result.TotalAffected;
                foreach (var (reason, count) in result.AffectedByReason)
                {
                    breakdown[reason] = breakdown.GetValueOrDefault(reason) + count;
                }
                tenantsProcessed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Error(ex,
                    "Auth: inbox-retention failed for realm {Slug}",
                    realm.Slug);
            }
        }

        context.Result = totalAffected == 0
            ? $"Nothing to do ({tenantsProcessed} tenant(s) checked)"
            : $"Touched {totalAffected} item(s) across {tenantsProcessed} tenant(s) — " +
              string.Join(", ", breakdown.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
