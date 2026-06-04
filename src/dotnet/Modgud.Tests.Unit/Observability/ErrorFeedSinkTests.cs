using Modgud.Authentication.AuthLog;
using Modgud.Infrastructure.Observability;
using Serilog.Events;
using Serilog.Parsing;

namespace Modgud.Tests.Unit.Observability;

/// <summary>
/// The Phase-5 (§B.3) capture filter. Default scope (Open Decision #7) is
/// Error+ from <c>Modgud.*</c> loggers only; the realm tag is read from the
/// <see cref="RealmLogEnricher"/>-stamped property. Both the level floor and
/// the source prefix are constructor params so the operator-configurable
/// behaviour is exercised here.
/// </summary>
public class ErrorFeedSinkTests
{
    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent Event(
        LogEventLevel level,
        string? sourceContext,
        string? realm,
        string template,
        Exception? exception = null,
        params LogEventProperty[] extra)
    {
        var props = new List<LogEventProperty>();
        if (sourceContext is not null) props.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));
        if (realm is not null) props.Add(new LogEventProperty("Realm", new ScalarValue(realm)));
        props.AddRange(extra);
        return new LogEvent(DateTimeOffset.UtcNow, level, exception, Parser.Parse(template), props);
    }

    private static (ErrorFeedSink sink, RealmErrorBuffer buffer) NewSink(
        LogEventLevel min = LogEventLevel.Error, string prefix = "Modgud")
    {
        var buffer = new RealmErrorBuffer();
        return (new ErrorFeedSink(buffer, min, prefix), buffer);
    }

    [Fact]
    public void ErrorFromModgudSource_IsCaptured()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, "Modgud.Authentication.Api.AccountEndpoints", "acme", "kaboom"));

        var rows = buffer.GetRecent("acme", 10);
        Assert.Single(rows);
        Assert.Equal("Error", rows[0].Level);
        Assert.Equal("kaboom", rows[0].Message);
        Assert.Equal("Modgud.Authentication.Api.AccountEndpoints", rows[0].SourceContext);
    }

    [Fact]
    public void FatalFromModgudSource_IsCaptured()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Fatal, "Modgud.Api.Program", "acme", "down"));
        Assert.Single(buffer.GetRecent("acme", 10));
    }

    [Fact]
    public void BelowFloor_IsIgnored()
    {
        var (sink, buffer) = NewSink(); // default floor = Error
        sink.Emit(Event(LogEventLevel.Warning, "Modgud.X", "acme", "just a warning"));
        sink.Emit(Event(LogEventLevel.Information, "Modgud.X", "acme", "fyi"));
        Assert.Empty(buffer.GetRecent("acme", 10));
    }

    [Fact]
    public void NonModgudSource_IsIgnored()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, "Microsoft.AspNetCore.Server", "acme", "framework error"));
        sink.Emit(Event(LogEventLevel.Error, "Npgsql.Connection", "acme", "db error"));
        Assert.Empty(buffer.GetRecent("acme", 10));
    }

    [Fact]
    public void NoSourceContext_IsIgnored()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, sourceContext: null, "acme", "static log error"));
        Assert.Empty(buffer.GetRecent("acme", 10));
    }

    [Fact]
    public void NoRealmProperty_FallsBackToSystem()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, "Modgud.X", realm: null, "no realm tagged"));
        Assert.Single(buffer.GetRecent("system", 10));
    }

    [Fact]
    public void RendersMessageTemplateArguments()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(
            LogEventLevel.Error, "Modgud.X", "acme", "failed for {Count} items",
            extra: new LogEventProperty("Count", new ScalarValue(42))));

        Assert.Equal("failed for 42 items", buffer.GetRecent("acme", 10)[0].Message);
    }

    [Fact]
    public void CapturesExceptionTypeAndMessage_NotTheGraph()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, "Modgud.X", "acme", "boom",
            exception: new InvalidOperationException("the cause")));

        Assert.Equal("InvalidOperationException: the cause", buffer.GetRecent("acme", 10)[0].Exception);
    }

    [Fact]
    public void NoException_LeavesExceptionNull()
    {
        var (sink, buffer) = NewSink();
        sink.Emit(Event(LogEventLevel.Error, "Modgud.X", "acme", "boom"));
        Assert.Null(buffer.GetRecent("acme", 10)[0].Exception);
    }

    [Fact]
    public void WidenedConfig_SinkFilter_AcceptsWarningFromAnySource()
    {
        // Operator widens the SINK's own filter: Warning floor + empty prefix.
        // This exercises the sink in isolation; in production Serilog's pipeline
        // floors (global Information + per-namespace Warning overrides) apply
        // additionally upstream, so framework sub-Warning events still wouldn't
        // reach the sink (see ErrorFeedSettings.MinimumLevel/SourcePrefix docs).
        // The Npgsql Warning here clears both the sink filter and (with the
        // shipped Npgsql→Warning override) the pipeline.
        var (sink, buffer) = NewSink(min: LogEventLevel.Warning, prefix: "");
        sink.Emit(Event(LogEventLevel.Warning, "Npgsql.Connection", "acme", "transient"));
        Assert.Single(buffer.GetRecent("acme", 10));
    }
}
