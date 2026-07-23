using Quartz;
using Modgud.Application.Inbox;

namespace Modgud.Api.Features.Inbox;

/// <summary>
/// Quartz wrapper around <see cref="IInboxRetentionService"/>. Runs daily
/// (default 03:00 UTC) and applies the owning realm's per-kind retention policy
/// stored in <see cref="InboxRetentionSettings"/>.
///
/// The job itself is intentionally dumb — no parameter schema, no per-run
/// config. Admins configure retention under <c>/admin/inbox-settings</c>,
/// and this job just orchestrates "when".
/// </summary>
[DisallowConcurrentExecution]
public class InboxRetentionJob(
    IInboxRetentionService retention) : IJob
{
    public const string Key = "inbox-retention";
    public const string Name = "Inbox Retention";
    public const string Description =
        "Applies this realm's inbox retention policy (configured under /admin/inbox-settings).";
    /// <summary>03:00 UTC every day — before the other two retention jobs.</summary>
    public const string DefaultCron = "0 0 3 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var result = await retention.ExecuteAsync(ct);

        context.Result = result.TotalAffected == 0
            ? "Nothing to do"
            : $"Touched {result.TotalAffected} item(s) — " +
              string.Join(", ", result.AffectedByReason.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
