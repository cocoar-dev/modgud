using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Audit;

/// <summary>
/// The streamless security/ops store (logging/audit redesign Track A, §A.5):
/// records about UNidentified actors + operational actions, in the system DB under
/// Art. 6(1)(f) legitimate interest. Two load-bearing claims are tested here:
/// (1) these records are NOT in the per-subject GDPR-erase path — they rely on the
/// short retention window, not erasure (Open Decision #4 = time-expiry only); and
/// (2) clearing the log is itself audited (audit-of-the-audit) with the operator's
/// identity. The control-plane test admin sees + clears the full cross-realm log.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditStoreTests : IntegrationTestBase
{
    public SecurityAuditStoreTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Streamless_record_survives_user_permanent_erase()
    {
        var ct = TestContext.Current.CancellationToken;

        // A registered user whose email also appears as the ATTEMPTED actor on a
        // pre-registration failed-login row in the streamless store.
        const string email = "boundary-victim@acme.com";
        var user = await Factory.CreateTestUserWithIdentityAsync("Boundary", "Victim", "bv", email);

        var rowId = Guid.NewGuid();
        await using (var write = GetTenantedDocumentSession("system"))
        {
            write.Store(new SecurityAuditEntry
            {
                Id = rowId,
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Warning",
                EventType = AuditEvents.LoginFailedUnknownUser,
                Actor = email,
                Ip = "203.0.113.50",
                Realm = "system",
                Message = $"Login failed for {email} — user not found or inactive",
            });
            await write.SaveChangesAsync(ct);
        }

        // Permanent-erase the user. The streamless store has no user stream to attach
        // to and is deliberately OUTSIDE the per-subject erase path.
        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var r = await gdpr.PermanentlyEraseAsync(user.Id, adminUserId: null, reason: "streamless-boundary-test", ct);
            Assert.False(r.IsError, r.IsError ? r.FirstError.Description : null);
        }

        // The streamless record SURVIVES the erase (it expires only via retention).
        await using (var read = GetTenantedDocumentSession("system"))
        {
            var survived = await read.LoadAsync<SecurityAuditEntry>(rowId, ct);
            Assert.NotNull(survived);
            Assert.Equal(email, survived!.Actor);
        }
    }

    [Fact]
    public async Task Clear_is_audited_with_the_operator_identity()
    {
        var ct = TestContext.Current.CancellationToken;

        // Something to clear.
        await using (var write = GetTenantedDocumentSession("system"))
        {
            write.Store(new SecurityAuditEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Warning",
                EventType = AuditEvents.LoginFailedUnknownUser,
                Actor = "to-be-cleared",
                Realm = "system",
                Message = "seed row for clear test",
            });
            await write.SaveChangesAsync(ct);
        }

        // Control-plane admin clears the full cross-realm log.
        var resp = await Client.DeleteAsync("/api/admin/auth-log", ct);
        resp.EnsureSuccessStatusCode();

        // The clear emits a typed audit.log_cleared record AFTER the wipe (the
        // forensic trail of who cleared what). It rides the best-effort async writer,
        // so poll briefly for it to land.
        var cleared = await PollForAsync(
            r => r.EventType == AuditEvents.AuditLogCleared, ct);

        Assert.NotNull(cleared);
        Assert.Equal("cleared", cleared!.Status);
        Assert.False(string.IsNullOrEmpty(cleared.Actor));
        Assert.NotEqual("(unknown)", cleared.Actor);
    }

    private async Task<SecurityAuditEntry?> PollForAsync(
        Func<SecurityAuditEntry, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 25; i++)
        {
            await using (var read = GetTenantedDocumentSession("system"))
            {
                var hit = (await read.Query<SecurityAuditEntry>().ToListAsync(ct))
                    .FirstOrDefault(predicate);
                if (hit is not null) return hit;
            }
            await Task.Delay(200, ct);
        }
        return null;
    }
}
