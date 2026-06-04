using Modgud.Infrastructure.Observability;

namespace Modgud.Tests.Unit.Observability;

/// <summary>
/// The Phase-5 (§B.3) load-bearing guarantee: the error feed uses an
/// independently-capped ring PER realm, so a noisy realm can never evict a
/// quiet realm's errors (the failure mode of the global
/// <see cref="ObservabilityActivityBuffer"/> ring this deliberately replaces).
/// </summary>
public class RealmErrorBufferTests
{
    private static ErrorLogEntry Entry(string realm, string message) =>
        new(DateTimeOffset.UtcNow, realm, "Error", message, Exception: null, SourceContext: "Modgud.X", TraceId: null);

    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        var buffer = new RealmErrorBuffer(capacityPerRealm: 10);
        buffer.Record(Entry("acme", "first"));
        buffer.Record(Entry("acme", "second"));
        buffer.Record(Entry("acme", "third"));

        var recent = buffer.GetRecent("acme", 10);

        Assert.Equal(new[] { "third", "second", "first" }, recent.Select(e => e.Message));
    }

    [Fact]
    public void GetRecent_UnknownRealm_ReturnsEmpty()
    {
        var buffer = new RealmErrorBuffer();
        buffer.Record(Entry("acme", "x"));

        Assert.Empty(buffer.GetRecent("globex", 10));
    }

    [Fact]
    public void GetRecent_RespectsLimit()
    {
        var buffer = new RealmErrorBuffer(capacityPerRealm: 10);
        for (var i = 0; i < 5; i++) buffer.Record(Entry("acme", $"m{i}"));

        Assert.Equal(2, buffer.GetRecent("acme", 2).Count);
    }

    [Fact]
    public void NoisyRealm_DoesNotEvictQuietRealm()
    {
        // The whole point of Phase 5's per-realm rings (§B.3). Cap is small; a
        // flood on one realm must leave another realm's single error intact.
        var buffer = new RealmErrorBuffer(capacityPerRealm: 3);

        buffer.Record(Entry("quiet", "the one quiet error"));
        for (var i = 0; i < 100; i++) buffer.Record(Entry("noisy", $"flood-{i}"));

        var quiet = buffer.GetRecent("quiet", 10);
        Assert.Single(quiet);
        Assert.Equal("the one quiet error", quiet[0].Message);

        // The noisy realm is independently capped at its own ring size.
        Assert.Equal(3, buffer.GetRecent("noisy", 100).Count);
    }

    [Fact]
    public void PerRealmCap_EvictsOwnOldest()
    {
        var buffer = new RealmErrorBuffer(capacityPerRealm: 2);
        buffer.Record(Entry("acme", "oldest"));
        buffer.Record(Entry("acme", "middle"));
        buffer.Record(Entry("acme", "newest"));

        var recent = buffer.GetRecent("acme", 10);
        Assert.Equal(new[] { "newest", "middle" }, recent.Select(e => e.Message));
    }

    [Fact]
    public void EntryRecorded_FiresForEachRecord_WithTheEntry()
    {
        var buffer = new RealmErrorBuffer();
        var seen = new List<ErrorLogEntry>();
        buffer.EntryRecorded += seen.Add;

        buffer.Record(Entry("acme", "a"));
        buffer.Record(Entry("globex", "b"));

        Assert.Equal(2, seen.Count);
        Assert.Equal("a", seen[0].Message);
        Assert.Equal("globex", seen[1].Realm);
    }

    [Fact]
    public void EntryRecorded_BuggySubscriber_DoesNotBreakRecording()
    {
        var buffer = new RealmErrorBuffer();
        buffer.EntryRecorded += _ => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => buffer.Record(Entry("acme", "still recorded")));

        Assert.Null(ex);
        Assert.Single(buffer.GetRecent("acme", 10));
    }
}
