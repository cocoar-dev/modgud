using System.Text.Json;
using Marten;
using Quartz;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Scheduling;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Trims the owning realm's <see cref="JobRunHistoryEntry"/> document table.
/// Quartz creates one instance per realm, with two independent caps tunable
/// from that realm's admin UI.
/// </summary>
[DisallowConcurrentExecution]
public class JobRunHistoryRetentionJob(
    IDocumentSession session,
    IJobRunHistoryRetentionService retention) : IJob
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
        var config = await BuildConfigAsync(session, Key, ct);
        var result = await retention.ExecuteAsync(config, ct);
        var total = result.DeletedByAge + result.DeletedByCount;
        context.Result = total == 0
            ? "Nothing to delete"
            : $"Deleted {total} entries (age: {result.DeletedByAge}, count: {result.DeletedByCount})";
    }

    internal static async Task<JobRunHistoryRetentionConfig> BuildConfigAsync(
        IQuerySession session,
        string configKey,
        CancellationToken ct)
    {
        var cfg = await session.LoadAsync<JobConfig>(configKey, ct);
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
