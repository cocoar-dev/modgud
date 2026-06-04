using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Infrastructure.Scheduling;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Trims the <see cref="JobRunHistoryEntry"/> document table for every active
/// realm. Iterates tenants via <see cref="IRealmCache"/>; each tenant gets
/// its own DI scope so the injected <see cref="IJobRunHistoryRetentionService"/>
/// opens its Marten session against the right tenant DB. Two independent caps —
/// both tunable in the admin UI without a code change.
/// </summary>
[DisallowConcurrentExecution]
public class JobRunHistoryRetentionJob(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache) : IJob
{
    public const string Key = "job-run-history-retention";
    public const string Name = "Job-Run-History Retention";
    public const string Description = "Trim run-history records (by age and/or by count per job).";
    /// <summary>03:30 UTC daily.</summary>
    public const string DefaultCron = "0 30 3 * * ?";

    public const string MaxAgeDaysKey = "maxAgeDays";
    public const string MaxEntriesPerJobKey = "maxEntriesPerJob";

    private const int DefaultMaxAgeDays = 30;

    public static IReadOnlyList<JobParameterField> GetParameterSchema() =>
    [
        new() {
            Key = MaxAgeDaysKey,
            Label = "Max. age in days",
            Type = JobParameterType.Number,
            Default = DefaultMaxAgeDays,
            Description = "Runs older than this are deleted. Empty = no age sweep.",
        },
        new() {
            Key = MaxEntriesPerJobKey,
            Label = "Max. entries per job",
            Type = JobParameterType.Number,
            Default = null,
            Placeholder = "unlimited",
            Description = "Keep only the N newest entries per job. Empty = no count cap.",
        },
    ];

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var realms = await realmCache.GetAllActiveAsync();

        int totalByAge = 0;
        int totalByCount = 0;
        int tenantsProcessed = 0;

        foreach (var realm in realms)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var _ = TenantContext.Enter(realm.Slug);

                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                var retention = scope.ServiceProvider.GetRequiredService<IJobRunHistoryRetentionService>();

                var config = await BuildConfigAsync(session, ct);
                var result = await retention.ExecuteAsync(config, ct);

                totalByAge += result.DeletedByAge;
                totalByCount += result.DeletedByCount;
                tenantsProcessed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Error(ex,
                    "job-run-history-retention failed for realm {Slug}",
                    realm.Slug);
            }
        }

        var total = totalByAge + totalByCount;
        context.Result = total == 0
            ? $"Nothing to delete ({tenantsProcessed} tenant(s) checked)"
            : $"Deleted {total} entries across {tenantsProcessed} tenant(s) (age: {totalByAge}, count: {totalByCount})";
    }

    private static async Task<JobRunHistoryRetentionConfig> BuildConfigAsync(IDocumentSession session, CancellationToken ct)
    {
        var cfg = await session.LoadAsync<JobConfig>(Key, ct);
        var raw = cfg?.Parameters ?? new Dictionary<string, object?>();
        return new JobRunHistoryRetentionConfig(
            MaxAgeDays: ReadInt(raw, MaxAgeDaysKey) ?? DefaultMaxAgeDays,
            MaxEntriesPerJob: ReadInt(raw, MaxEntriesPerJobKey));
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement el => el.ValueKind switch
            {
                JsonValueKind.Number => el.TryGetInt32(out var n) ? n : (int?)null,
                JsonValueKind.String when int.TryParse(el.GetString(), out var n) => n,
                _ => null,
            },
            string s when int.TryParse(s, out var n) => n,
            string s when string.IsNullOrWhiteSpace(s) => null,
            _ => null,
        };
    }
}
