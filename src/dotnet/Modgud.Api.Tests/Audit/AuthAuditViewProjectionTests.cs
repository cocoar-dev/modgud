using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Events;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Audit;

/// <summary>
/// Phase 0 scaffold proof: the <see cref="AuthAuditViewProjection"/> folds
/// user-stream auth/lifecycle events into flat, per-event <see cref="AuthAuditView"/>
/// rows carrying typed category + event-type + realm — without copying PII payloads.
/// Mirrors the explicit daemon-rebuild pattern from <c>ProjectionRebuildTests</c>
/// (MasterTableTenancy → build the daemon for the "system" realm DB).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthAuditViewProjectionTests : IntegrationTestBase
{
    public AuthAuditViewProjectionTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Projects_user_stream_events_into_flat_typed_audit_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Audit", "Scaffold", "as", "audit-scaffold@acme.com");

        const string ip = "203.0.113.7";
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            // A login marker (carries method + ip) + a password change, plus an
            // aggregated failure-streak record — all on the user's stream.
            session.Events.Append(user.Id, new UserLoggedInEvent(user.Id, ip, "password"));
            session.Events.Append(user.Id, new UserPasswordChangedEvent(user.Id, null));
            session.Events.Append(user.Id, new UserLoginFailuresObservedEvent(user.Id, 3, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(ct);
        }

        await RebuildAuthAuditAsync(ct);

        await using var qs = GetTenantedSession();
        var rows = await qs.Query<AuthAuditView>().Where(r => r.UserId == user.Id).ToListAsync(ct);

        Assert.NotEmpty(rows);

        // The login marker projects to an authentication row that keeps the method + IP.
        Assert.Contains(rows, r =>
            r.EventType == AuditEvents.LoginSucceeded &&
            r.Category == AuditCategories.Authentication &&
            r.Ip == ip &&
            r.Method == "password" &&
            r.UserId == user.Id);

        // The aggregated failure streak projects with its count (Decision (b)).
        Assert.Contains(rows, r =>
            r.EventType == AuditEvents.LoginFailuresObserved &&
            r.Category == AuditCategories.Authentication &&
            r.Count == 3);

        // The password change projects to an account-category row.
        Assert.Contains(rows, r =>
            r.EventType == AuditEvents.AccountPasswordChanged &&
            r.Category == AuditCategories.Account);

        // User creation already produced account-lifecycle rows on the stream.
        Assert.Contains(rows, r => r.Category == AuditCategories.Account);

        // Per-tenant view: every row is realm-tagged (here, the system realm).
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Realm)));
    }

    private async Task RebuildAuthAuditAsync(CancellationToken ct)
    {
        // MasterTableTenancy disables the default tenant — build the daemon for the
        // "system" realm DB explicitly (mirrors ProjectionRebuildTests / RecoveryCli).
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        using var daemon = await store.BuildProjectionDaemonAsync("system");
        await daemon.RebuildProjectionAsync<AuthAuditViewProjection>(TimeSpan.FromMinutes(2), ct);
    }
}
