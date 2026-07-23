using System.Text.Json;
using Marten;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Scheduling;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Single deployment-wide hard-prune for PII-free platform events in the
/// non-tenanted Global Store.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PlatformAuditPruneJob(IGlobalStore globalStore) : IJob
{
    public const string Key = "platform-audit-prune";
    public const string Name = "Platform Audit Prune";
    public const string Description =
        "Hard-deletes PII-free deployment-wide platform events after the configured retention period.";
    public const string DefaultCron = "0 15 2 * * ?";
    public const string RetentionDaysKey = "retentionDays";
    public const int DefaultRetentionDays = 365;

    public static IReadOnlyList<JobParameterField> GetParameterSchema() =>
    [
        new()
        {
            Key = RetentionDaysKey,
            Label = "Retention in days",
            Type = JobParameterType.Number,
            Default = DefaultRetentionDays,
            Description = "Deployment-wide platform-event retention (1–3650 days).",
        },
    ];

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        await using var session = globalStore.LightweightSession();
        var config = await session.LoadAsync<JobConfig>(Key, ct);
        var retentionDays = ReadInt(config?.Parameters, RetentionDaysKey) ?? DefaultRetentionDays;
        if (retentionDays is < 1 or > 3650)
            throw new JobExecutionException(
                $"Platform audit retention must be between 1 and 3650 days, got {retentionDays}.");

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var doomed = await session.Query<PlatformAuditEvent>()
            .CountAsync(x => x.Timestamp < cutoff, ct);
        session.DeleteWhere<PlatformAuditEvent>(x => x.Timestamp < cutoff);
        await session.SaveChangesAsync(ct);

        context.Result = doomed == 0
            ? "No entries to prune"
            : $"Pruned {doomed} platform event(s) older than {retentionDays} day(s)";
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, object?>? values,
        string key)
    {
        if (values is null || !values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            double number => checked((int)number),
            JsonElement { ValueKind: JsonValueKind.Number } json
                when json.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } json
                when int.TryParse(json.GetString(), out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => null,
        };
    }
}
