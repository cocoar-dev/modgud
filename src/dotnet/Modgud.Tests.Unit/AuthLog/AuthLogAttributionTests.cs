using Modgud.Authentication.Api.Admin;
using Modgud.Authentication.AuthLog;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Modgud.Tests.Unit.AuthLog;

/// <summary>
/// The realm attribution that scopes the admin auth-log read: the enricher
/// captures the ambient realm at log time, and the sink reads it off the event
/// onto the persisted document. These are the two deterministic seams of the
/// AuthLog tenant-visibility fix (the realm filtering in the read endpoint is a
/// trivial Where over this column).
/// </summary>
public class AuthLogAttributionTests
{
    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent AuthEvent(string template, params LogEventProperty[] props) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Warning, exception: null, Parser.Parse(template), props);

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    // ── Enricher ────────────────────────────────────────────────────────

    [Fact]
    public void Enricher_StampsAmbientRealm()
    {
        var evt = AuthEvent("Auth: signing key rotated");

        using (TenantContext.Enter("acme"))
            new RealmLogEnricher().Enrich(evt, new TestPropertyFactory());

        Assert.True(evt.Properties.TryGetValue("Realm", out var v));
        Assert.Equal("acme", ((ScalarValue)v).Value);
    }

    [Fact]
    public void Enricher_NoAmbientTenant_FallsBackToSystem()
    {
        var evt = AuthEvent("Auth: something happened");

        // No TenantContext.Enter — Current falls back to the system tenant, so
        // background / no-tenant events are attributed to "system" (not orphaned).
        new RealmLogEnricher().Enrich(evt, new TestPropertyFactory());

        Assert.True(evt.Properties.TryGetValue("Realm", out var v));
        Assert.Equal("system", ((ScalarValue)v).Value);
    }

    // ── Sink ────────────────────────────────────────────────────────────

    [Fact]
    public void Sink_CapturesRealmProperty_OntoDocument()
    {
        var sink = new AuthLogSink();

        sink.Emit(AuthEvent("Auth: login successful {UserName}",
            new LogEventProperty("UserName", new ScalarValue("bob")),
            new LogEventProperty("Realm", new ScalarValue("acme"))));

        Assert.True(sink.Reader.TryRead(out var doc));
        Assert.Equal("acme", doc!.Realm);
        Assert.Equal("bob", doc.UserName);
    }

    [Fact]
    public void Sink_NoRealmProperty_LeavesRealmNull()
    {
        var sink = new AuthLogSink();

        sink.Emit(AuthEvent("Auth: background event"));

        Assert.True(sink.Reader.TryRead(out var doc));
        Assert.Null(doc!.Realm);
    }

    [Fact]
    public void Sink_IgnoresNonAuthEvents()
    {
        var sink = new AuthLogSink();

        sink.Emit(AuthEvent("Some unrelated log {Realm}",
            new LogEventProperty("Realm", new ScalarValue("acme"))));

        Assert.False(sink.Reader.TryRead(out _)); // only "Auth:"-prefixed events are persisted
    }

    // ── Read scoping (AuthLogEndpoints.ScopeToCallerRealm over the streamless store) ──

    private static IQueryable<SecurityAuditEntry> Rows() => new[]
    {
        new SecurityAuditEntry { Message = "a", Realm = "system", PlatformOnly = false },
        new SecurityAuditEntry { Message = "b", Realm = "acme", PlatformOnly = false },
        new SecurityAuditEntry { Message = "c", Realm = "globex", PlatformOnly = false },
        new SecurityAuditEntry { Message = "p", Realm = "acme", PlatformOnly = true },
    }.AsQueryable();

    [Fact]
    public void Scope_ControlPlane_SeesEveryRealm_IncludingPlatformOnly()
    {
        var result = AuthLogEndpoints.ScopeToCallerRealm(Rows(), "system", callerIsControlPlane: true).ToList();
        Assert.Equal(4, result.Count); // the control-plane realm sees the full cross-realm log, platform-only included
    }

    [Fact]
    public void Scope_TenantRealm_SeesOnlyOwnRealm_TenantVisibleOnly()
    {
        var result = AuthLogEndpoints.ScopeToCallerRealm(Rows(), "acme", callerIsControlPlane: false).ToList();
        Assert.Single(result);
        Assert.Equal("b", result[0].Message); // own realm, tenant-visible — NOT the platform-only "p" row
    }

    [Fact]
    public void Scope_TenantRealm_NeverSeesPlatformOnly()
    {
        // A control-plane-only operational row in the caller's OWN realm must still
        // be hidden from a tenant realm-admin.
        var result = AuthLogEndpoints.ScopeToCallerRealm(Rows(), "acme", callerIsControlPlane: false).ToList();
        Assert.DoesNotContain(result, r => r.PlatformOnly);
    }

    [Fact]
    public void Scope_NonControlPlaneSystemRealm_SeesOnlyItsOwn()
    {
        // The leak guard: a realm named "system" that is NOT the control-plane
        // holder (e.g. after a control-plane transfer) must NOT see other realms.
        var result = AuthLogEndpoints.ScopeToCallerRealm(Rows(), "system", callerIsControlPlane: false).ToList();
        Assert.Single(result);
        Assert.Equal("system", result[0].Realm);
    }
}
