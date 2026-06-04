using System.Diagnostics;
using Modgud.Infrastructure.Observability;
using Serilog.Core;
using Serilog.Events;

namespace Modgud.Authentication.AuthLog;

/// <summary>
/// Serilog sink that feeds the in-app per-realm live error feed
/// (logging/audit redesign Phase 5, §B.3). Captures qualifying log events into
/// the process-local <see cref="RealmErrorBuffer"/>, which the
/// <c>ObservabilityHub.LogsSubscribe</c> stream and the
/// <c>/api/admin/observability/errors</c> snapshot read per realm.
///
/// <para><b>Scope (Open Decision #7 — operator choice):</b> by default only
/// <c>Error</c>+ events from <c>Modgud.*</c> loggers are captured — the quiet
/// "an application error happened on my realm" feed. Framework loggers
/// (Marten / Npgsql / Wolverine / Microsoft / System) are excluded, so
/// infrastructure failures surface in Console / File / OpenObserve but not in
/// this in-app panel. Both the level floor and the source prefix are
/// configurable (<c>Observability__ErrorFeed__MinimumLevel</c> /
/// <c>__SourcePrefix</c>) so this can be widened without a code change.</para>
///
/// <para>The realm tag comes from the <see cref="RealmLogEnricher"/>-stamped
/// <c>Realm</c> property (falls back to <c>system</c>). Records are rendered
/// and length-capped here so the buffer keeps only display-safe strings and a
/// bounded footprint — no live <see cref="LogEvent"/> / exception graph is
/// retained.</para>
///
/// <para>Best-effort: a capture failure must never break logging, so
/// <see cref="Emit"/> swallows. This feed does NOT pass through the OTel
/// collector redaction — the call-site PII belt + per-realm read scoping are
/// the controls (mirrors the streamless security store; see §B.3).</para>
/// </summary>
public sealed class ErrorFeedSink : ILogEventSink
{
    private const int MaxMessageLength = 1000;
    private const int MaxExceptionLength = 1000;

    private readonly RealmErrorBuffer _buffer;
    private readonly LogEventLevel _minimumLevel;
    private readonly string _sourcePrefix;

    public ErrorFeedSink(RealmErrorBuffer buffer, LogEventLevel minimumLevel, string sourcePrefix)
    {
        _buffer = buffer;
        _minimumLevel = minimumLevel;
        _sourcePrefix = sourcePrefix;
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            if (logEvent.Level < _minimumLevel) return;

            // Source filter: only loggers under the configured prefix. A log
            // with no SourceContext (e.g. a static Log.Error) is excluded.
            var sourceContext = ReadScalarString(logEvent, "SourceContext");
            if (sourceContext is null ||
                !sourceContext.StartsWith(_sourcePrefix, StringComparison.Ordinal))
                return;

            var realm = ReadScalarString(logEvent, "Realm") ?? "system";
            var message = Truncate(logEvent.RenderMessage(), MaxMessageLength);
            var exception = logEvent.Exception is { } ex
                ? Truncate($"{ex.GetType().Name}: {ex.Message}", MaxExceptionLength)
                : null;

            // Trace correlation with OpenObserve (the OTLP sink reads the same
            // ambient Activity). Present only when a trace is in flight.
            var traceId = Activity.Current?.TraceId.ToString();

            _buffer.Record(new ErrorLogEntry(
                logEvent.Timestamp,
                realm,
                logEvent.Level.ToString(),
                message,
                exception,
                sourceContext,
                traceId));
        }
        catch
        {
            // Never let the live-feed capture break the logging pipeline.
        }
    }

    private static string? ReadScalarString(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var value)
           && value is ScalarValue { Value: string s }
            ? s
            : null;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
