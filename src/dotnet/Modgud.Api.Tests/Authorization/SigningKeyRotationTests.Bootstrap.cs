using Marten;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// FK-08 — a realm's first (or, after a lost tenant DB, a fresh) signing key appeared
/// without any trace, while rotate, schedule and revoke write a required audit event.
/// An unexpected fresh key — every outstanding token of the realm dead — is now visible.
/// </summary>
public partial class SigningKeyRotationTests
{
    [Fact]
    public async Task Bootstrapping_the_first_signing_key_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var kid = (await NewStore(new TestClock(DateTimeOffset.UtcNow)).GetActiveSigningCredentialsAsync(Realm, ct)).Key.KeyId;

        await using var session = GetTenantedSession(Realm);
        var record = await session.Query<RealmSecurityAuditEvent>()
            .FirstOrDefaultAsync(e => e.EventType == AuditEvents.SigningKeyBootstrapped && e.KeyId == kid, ct);
        Assert.NotNull(record);
    }
}
