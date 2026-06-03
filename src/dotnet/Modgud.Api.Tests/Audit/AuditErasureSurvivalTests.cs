using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;

namespace Modgud.Api.Tests.Audit;

/// <summary>
/// The load-bearing GDPR claim of the audit redesign: a permanently-erased user is
/// MASKED, not deleted — so their audit rows must SURVIVE, de-identified (Ip null),
/// and survive a full projection rebuild. Rebuild durability comes from
/// <c>AuthAuditViewProjection.IncludeArchivedEvents = true</c> (the masked events are
/// archived, not deleted); live freshness comes from the erase-time Ip refresh in
/// <c>GdprService.PerformPermanentEraseAsync</c>. See §A.4.2 of the design doc.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuditErasureSurvivalTests : IntegrationTestBase
{
    public AuditErasureSurvivalTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Erased_user_audit_rows_survive_deidentified_across_rebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Erase", "Audit", "ea", "erase-audit@acme.com");

        const string ip = "203.0.113.9";
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(user.Id, new UserLoggedInEvent(user.Id, ip, "password"));
            await session.SaveChangesAsync(ct);
        }
        await RebuildAuthAuditAsync(ct);

        // Before erase: the login row carries the IP.
        await using (var qs = GetTenantedSession())
        {
            var before = await qs.Query<AuthAuditView>().Where(r => r.UserId == user.Id).ToListAsync(ct);
            Assert.Contains(before, r => r.EventType == AuditEvents.LoginSucceeded && r.Ip == ip);
        }

        // GDPR permanent erase — masks + archives the user stream.
        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var r = await gdpr.PermanentlyEraseAsync(user.Id, adminUserId: null, reason: "audit-survival-test", ct);
            Assert.False(r.IsError, r.IsError ? r.FirstError.Description : null);
        }

        // Live view: rows SURVIVE, de-identified (Ip null) — NOT deleted.
        await using (var qs = GetTenantedSession())
        {
            var live = await qs.Query<AuthAuditView>().Where(r => r.UserId == user.Id).ToListAsync(ct);
            Assert.Contains(live, r => r.EventType == AuditEvents.LoginSucceeded);
            Assert.All(live, r => Assert.Null(r.Ip));
        }

        // Durable across a full rebuild: IncludeArchivedEvents regenerates the rows
        // from the masked archived events (still de-identified).
        await RebuildAuthAuditAsync(ct);
        await using (var qs = GetTenantedSession())
        {
            var afterRebuild = await qs.Query<AuthAuditView>().Where(r => r.UserId == user.Id).ToListAsync(ct);
            Assert.Contains(afterRebuild, r => r.EventType == AuditEvents.LoginSucceeded && r.Ip == null);
        }
    }

    private async Task RebuildAuthAuditAsync(CancellationToken ct)
    {
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        using var daemon = await store.BuildProjectionDaemonAsync("system");
        await daemon.RebuildProjectionAsync<AuthAuditViewProjection>(TimeSpan.FromMinutes(2), ct);
    }
}
