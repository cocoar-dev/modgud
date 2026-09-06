using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.Sessions;

/// <summary>
/// The node-local connection registry behind instant session revocation and,
/// since ADR 0022, the periodic DB re-validation sweep: <c>Snapshot</c> must
/// list every session with connections here together with its realm, and
/// <c>Revoke</c> must abort every connection of that session.
/// </summary>
public class BrowserSessionConnectionRegistryTests
{
    [Fact]
    public void Snapshot_lists_sessions_with_their_realm()
    {
        var sut = new BrowserSessionConnectionRegistry();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        using var _ = sut.Register(s1, "c1", Context("acme"));
        using var __ = sut.Register(s1, "c2", Context("acme"));
        using var ___ = sut.Register(s2, "c3", Context("globex"));

        var snapshot = sut.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, c => c.SessionId == s1 && c.Realm == "acme");
        Assert.Contains(snapshot, c => c.SessionId == s2 && c.Realm == "globex");
    }

    [Fact]
    public void Disposing_the_last_registration_removes_the_session_from_the_snapshot()
    {
        var sut = new BrowserSessionConnectionRegistry();
        var s1 = Guid.NewGuid();
        var registration = sut.Register(s1, "c1", Context("acme"));

        registration.Dispose();

        Assert.Empty(sut.Snapshot());
    }

    [Fact]
    public void Revoke_aborts_every_connection_of_the_session_and_forgets_it()
    {
        var sut = new BrowserSessionConnectionRegistry();
        var s1 = Guid.NewGuid();
        var a = new AbortTracker();
        var b = new AbortTracker();
        using var _ = sut.Register(s1, "c1", Context("acme", a));
        using var __ = sut.Register(s1, "c2", Context("acme", b));

        sut.Revoke(s1);

        Assert.True(a.Aborted);
        Assert.True(b.Aborted);
        Assert.Empty(sut.Snapshot());
    }

    private static HttpContext Context(string realm, AbortTracker? tracker = null)
    {
        var context = new DefaultHttpContext();
        context.Items[TenantConstants.HttpContextTenantIdKey] = realm;
        if (tracker is not null)
            context.Features.Set<IHttpRequestLifetimeFeature>(tracker);
        return context;
    }

    private sealed class AbortTracker : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}
