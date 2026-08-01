using Modgud.Authentication.Sessions;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

[DisallowConcurrentExecution]
public sealed class SessionPruneJob(
    ISessionService browserSessions,
    IClientSessionService clientSessions) : IJob
{
    public const string Key = "session-prune";
    public const string Name = "Session Retention";
    public const string Description = "Remove expired browser and native OAuth client/device sessions in this realm.";
    public const string DefaultCron = "0 15 4 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var browser = await browserSessions.PruneExpiredAsync(context.CancellationToken);
        var clients = await clientSessions.PruneExpiredAsync(context.CancellationToken);
        context.Result = $"Deleted {browser} browser sessions and {clients} client sessions";
    }
}
