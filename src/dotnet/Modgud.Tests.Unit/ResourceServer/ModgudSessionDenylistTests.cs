using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

/// <summary>ADR 0021 increment 2 — the in-memory denylist of ended sessions.</summary>
public class ModgudSessionDenylistTests
{
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_ended_session_is_revoked_until_its_expiry_then_forgotten()
    {
        var clock = new TestClock(T0);
        var denylist = new ModgudSessionDenylist(clock);

        Assert.False(denylist.IsRevoked("sid-1"));
        denylist.Revoke("sid-1", T0.AddMinutes(65));
        Assert.True(denylist.IsRevoked("sid-1"));
        Assert.Equal(1, denylist.Count);

        clock.Now = T0.AddMinutes(66);
        Assert.False(denylist.IsRevoked("sid-1"));
        Assert.Equal(0, denylist.Count);
    }

    [Fact]
    public void A_later_end_extends_but_never_shortens_the_entry()
    {
        var clock = new TestClock(T0);
        var denylist = new ModgudSessionDenylist(clock);
        denylist.Revoke("sid", T0.AddMinutes(60));
        denylist.Revoke("sid", T0.AddMinutes(30));
        clock.Now = T0.AddMinutes(45);
        Assert.True(denylist.IsRevoked("sid"));
        denylist.Revoke("sid", T0.AddMinutes(90));
        clock.Now = T0.AddMinutes(75);
        Assert.True(denylist.IsRevoked("sid"));
    }

    [Fact]
    public void Prune_drops_expired_entries_and_sync_is_stamped()
    {
        var clock = new TestClock(T0);
        var denylist = new ModgudSessionDenylist(clock);
        Assert.Null(denylist.LastSyncedAt);
        denylist.Revoke("a", T0.AddMinutes(1));
        denylist.Revoke("b", T0.AddMinutes(10));
        clock.Now = T0.AddMinutes(5);
        Assert.Equal(1, denylist.Prune());
        Assert.Equal(1, denylist.Count);
        denylist.MarkSynced();
        Assert.Equal(clock.Now, denylist.LastSyncedAt);
    }
}
