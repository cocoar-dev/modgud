using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>
/// Follows the Application change feed and puts every ended session on the denylist.
/// Starts from a fresh snapshot cursor (live sessions are of no interest — only ends
/// are), polls with the committed cursor, takes a new snapshot on reset, and retries
/// with a fixed delay on any failure. Runs for the lifetime of the host.
/// </summary>
internal sealed class ModgudSessionRevocationWorker(
    ModgudSessionFeedClient feed,
    ModgudSessionDenylist denylist,
    ModgudSessionRevocationOptions options,
    TimeProvider clock,
    ILogger<ModgudSessionRevocationWorker> logger) : BackgroundService
{
    private string? _cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Modgud: session revocation follows the change feed of App {AppId}.", options.AppId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await StepAsync(stoppingToken);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Modgud: change-feed read failed; retrying in {Delay}. Revocations are not enforced until the feed is reachable again.", options.RetryDelay);
                await Task.Delay(options.RetryDelay, clock, stoppingToken);
            }
        }
    }

    /// <summary>One read; returns how long to wait before the next.</summary>
    internal async Task<TimeSpan> StepAsync(CancellationToken ct)
    {
        if (_cursor is null)
        {
            _cursor = await feed.SnapshotCursorAsync(ct);
            logger.LogInformation("Modgud: session revocation anchored at a fresh change-feed cursor.");
        }

        var batch = await feed.ReadAsync(_cursor, ct);
        if (batch.ResetRequired)
        {
            _cursor = null;
            return TimeSpan.Zero;
        }

        var until = clock.GetUtcNow() + options.AccessTokenLifetime + options.ClockSkew;
        var revoked = 0;
        foreach (var message in batch.Messages)
        {
            if (message.EntityKind != ModgudSessionFeedDefaults.SessionEntityKind) continue;
            if (message.ChangeKind != "Deleted") continue;
            if (string.IsNullOrEmpty(message.SessionId)) continue;
            denylist.Revoke(message.SessionId, until);
            revoked++;
        }
        if (revoked > 0)
            logger.LogDebug("Modgud: {Count} session(s) ended; denylist now holds {Total}.", revoked, denylist.Count);

        denylist.Prune();
        denylist.MarkSynced();
        if (batch.LastCursor is not null) _cursor = batch.LastCursor;

        if (batch.FeedEnded)
        {
            logger.LogWarning("Modgud: the change feed of App {AppId} ended (feed disabled or App deleted); retrying in {Delay}.", options.AppId, options.RetryDelay);
            _cursor = null;
            return options.RetryDelay;
        }
        return batch.HasMore ? TimeSpan.Zero : options.PollInterval;
    }
}
