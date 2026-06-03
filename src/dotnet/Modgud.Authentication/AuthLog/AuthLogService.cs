using System.Threading.Channels;
using Marten;
using Serilog.Core;
using Serilog.Events;
using Modgud.Authentication.AuthLog;

namespace Modgud.Authentication.AuthLog;

/// <summary>
/// Serilog sink that captures "Auth:" prefixed log entries
/// and forwards them to a channel for async DB persistence.
/// </summary>
public class AuthLogSink : ILogEventSink
{
    private readonly Channel<AuthLogDocument> _channel = Channel.CreateUnbounded<AuthLogDocument>();

    public ChannelReader<AuthLogDocument> Reader => _channel.Reader;

    public void Emit(LogEvent logEvent)
    {
        if (!logEvent.MessageTemplate.Text.StartsWith("Auth:")) return;

        string? userName = null;
        string? ip = null;
        string? realm = null;

        if (logEvent.Properties.TryGetValue("UserName", out var userProp))
            userName = userProp.ToString().Trim('"');
        if (logEvent.Properties.TryGetValue("IP", out var ipProp))
            ip = ipProp.ToString().Trim('"');
        // Realm is stamped by RealmLogEnricher from the ambient TenantContext at
        // emit time (the sink itself runs tenant-less in a BackgroundService).
        if (logEvent.Properties.TryGetValue("Realm", out var realmProp))
            realm = realmProp.ToString().Trim('"');

        // Render the message with placeholder values substituted in
        // (`User={UserName}` → `User=admin`) instead of persisting the raw
        // template — otherwise the audit-log message column reads as
        // `Login successful User={UserName}` and an admin can't tell who
        // logged in without joining columns mentally. UserName and IP are
        // already extracted into their own columns above; we still leave
        // them in the rendered message because the rest of the message
        // can reference them by name (e.g. `Login failed for {UserName}
        // from {IP} — wrong password`).
        var rendered = logEvent.RenderMessage();
        var message = rendered.StartsWith("Auth: ") ? rendered["Auth: ".Length..] : rendered;
        message = message.TrimEnd('.').Trim();

        _channel.Writer.TryWrite(new AuthLogDocument
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level switch
            {
                LogEventLevel.Warning => "Warning",
                LogEventLevel.Error or LogEventLevel.Fatal => "Error",
                _ => "Info",
            },
            Message = message,
            UserName = userName,
            Ip = ip,
            Realm = realm,
        });
    }
}

/// <summary>
/// Background service that drains auth log entries from the channel into Marten
/// and periodically cleans up entries older than 7 days.
/// </summary>
public class AuthLogPersistenceService(IServiceProvider services, AuthLogSink sink) : BackgroundService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = CleanupLoop(stoppingToken);

        await foreach (var entry in sink.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                // AuthLog runs out-of-band in a HostedService — there is no
                // HttpContext to drive tenant resolution, so target the master
                // ("system") tenant explicitly. AuthLog documents live in the
                // master DB by design (cross-tenant audit log).
                await using var session = scope.ServiceProvider
                    .GetRequiredService<IDocumentStore>()
                    .LightweightSession("system");

                session.Store(entry);
                await session.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Error(ex, "Failed to persist auth log entry");
            }
        }
    }

    private async Task CleanupLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                // AuthLog runs out-of-band in a HostedService — there is no
                // HttpContext to drive tenant resolution, so target the master
                // ("system") tenant explicitly. AuthLog documents live in the
                // master DB by design (cross-tenant audit log).
                await using var session = scope.ServiceProvider
                    .GetRequiredService<IDocumentStore>()
                    .LightweightSession("system");

                var cutoff = DateTimeOffset.UtcNow - RetentionPeriod;
                session.DeleteWhere<AuthLogDocument>(x => x.Timestamp < cutoff);
                await session.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Error(ex, "Failed to cleanup old auth log entries");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }
}
