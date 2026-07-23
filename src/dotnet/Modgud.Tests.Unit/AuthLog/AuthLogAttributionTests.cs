using Modgud.Authentication.AuthLog;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Modgud.Tests.Unit.AuthLog;

public class AuthLogAttributionTests
{
    private static readonly MessageTemplateParser Parser = new();

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    [Fact]
    public void Enricher_StampsAmbientRealm()
    {
        var evt = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            null,
            Parser.Parse("security operation"),
            []);

        using (TenantContext.Enter("acme"))
            new RealmLogEnricher().Enrich(evt, new TestPropertyFactory());

        Assert.Equal("acme", ((ScalarValue)evt.Properties["Realm"]).Value);
    }

    [Fact]
    public void Realm_event_has_no_simulated_realm_or_free_text_fields()
    {
        var names = typeof(RealmSecurityAuditEvent).GetProperties()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Realm", names);
        Assert.DoesNotContain("Actor", names);
        Assert.DoesNotContain("Reason", names);
        Assert.DoesNotContain("Message", names);
    }

    [Fact]
    public void Platform_event_type_cannot_hold_forensic_pii()
    {
        var names = typeof(PlatformAuditEvent).GetProperties()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ActorSubjectId", names);
        Assert.DoesNotContain("TargetSubjectId", names);
        Assert.DoesNotContain("IpAddress", names);
        Assert.DoesNotContain("UserAgent", names);
        Assert.DoesNotContain("UnknownIdentifierFingerprint", names);
        Assert.DoesNotContain("OAuthClientId", names);
        Assert.DoesNotContain("SessionId", names);
    }
}
