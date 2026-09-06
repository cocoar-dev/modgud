using Modgud.Authentication.Sessions;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

[DisallowConcurrentExecution]
public sealed class SessionPruneJob(
    ISessionService browserSessions,
    IClientSessionService clientSessions,
    ISessionGrantService sessionGrants) : IJob
{
    public const string Key = "session-prune";
    public const string Name = "Session Retention";
    public const string Description = "Remove expired browser and native OAuth client/device sessions in this realm, and any relying-party grant left without a session.";
    public const string DefaultCron = "0 15 4 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var browser = await browserSessions.PruneExpiredAsync(context.CancellationToken);
        var clients = await clientSessions.PruneExpiredAsync(context.CancellationToken);
        // ADR 0021 — grants die with their session; this catches any left behind.
        var orphans = await sessionGrants.SweepOrphansAsync(context.CancellationToken);
        context.Result = $"Deleted {browser} browser sessions and {clients} client sessions"
                         + (orphans == 0 ? "" : $"; removed {orphans} orphaned session grant(s)");
    }
}
