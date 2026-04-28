using System.Threading.Channels;
using Marten;
using Serilog.Core;
using Serilog.Events;
using TimeToDo.Authentication.AuthLog;

namespace TimeToDo.Authentication.AuthLog;

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

        if (logEvent.Properties.TryGetValue("UserName", out var userProp))
            userName = userProp.ToString().Trim('"');
        if (logEvent.Properties.TryGetValue("IP", out var ipProp))
            ip = ipProp.ToString().Trim('"');

        var rawMessage = logEvent.MessageTemplate.Text;
        var message = rawMessage.StartsWith("Auth: ") ? rawMessage["Auth: ".Length..] : rawMessage;

        message = message
            .Replace(" UserName={UserName}", "")
            .Replace(" IP={IP}", "")
            .Replace(" Locked={Locked}", "")
            .Replace(" UserId={UserId}", "")
            .Replace(".", "")
            .Trim();

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
                await using var session = scope.ServiceProvider
                    .GetRequiredService<IDocumentStore>()
                    .LightweightSession();

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
                await using var session = scope.ServiceProvider
                    .GetRequiredService<IDocumentStore>()
                    .LightweightSession();

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
