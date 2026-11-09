using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Infrastructure.Audit;

namespace Modgud.Tests.Unit.Audit;

/// <summary>
/// B9-J03 — the single audit writer retried a whole abuse batch until it persisted, so one
/// realm whose persistence kept failing blocked every other realm's signals until the shared
/// channel overflowed. Each realm group now persists on its own, with bounded attempts.
/// </summary>
public sealed class AbuseWriterIsolationTests
{
    [Fact]
    public async Task A_failing_realm_does_not_block_the_others_and_gives_up_after_bounded_attempts()
    {
        var batch = new[]
        {
            SecurityAuditEnvelope.ForRealm("bad", new SecurityAuditRecord { EventType = "e" }, AuditDurabilityClass.Abuse),
            SecurityAuditEnvelope.ForRealm("good", new SecurityAuditRecord { EventType = "e" }, AuditDurabilityClass.Abuse),
        };
        var persisted = new List<string>();
        var badAttempts = 0;

        var run = SecurityAuditWriter.PersistPerRealmAsync(batch, group =>
        {
            var realm = group.First().RealmSlug!;
            if (realm == "bad") { badAttempts++; throw new InvalidOperationException("realm db down"); }
            persisted.Add(realm);
            return Task.CompletedTask;
        }, _ => TimeSpan.Zero, NullLogger.Instance, CancellationToken.None);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(run, finished);
        Assert.Equal(["good"], persisted);
        Assert.Equal(SecurityAuditWriter.MaxAbuseAttempts, badAttempts);
    }
}
