using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Events;

namespace Modgud.Api.Tests.Audit;

/// <summary>
/// Integration test for <c>GET /api/admin/audit</c> — the tenant GDPR-audit read
/// surface over the per-realm <see cref="AuthAuditView"/>. Verifies it serves the
/// caller-realm rows (the authed client is a realm-admin) and honours the category
/// filter. Realm isolation itself is physical (per-tenant DB), so it isn't re-tested
/// here.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuditEndpointTests : IntegrationTestBase
{
    public AuditEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record AuditRowDto(string EventType, string Category, string? Ip, string? User);

    [Fact]
    public async Task Get_returns_realm_audit_rows_and_honours_category_filter()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a login (authentication) + a password change (account) on a user stream.
        var user = await Factory.CreateTestUserWithIdentityAsync("Audit", "Endpoint", "ae", "audit-ep@acme.com");
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(user.Id, new UserLoggedInEvent(user.Id, "203.0.113.1", "password"));
            session.Events.Append(user.Id, new UserPasswordChangedEvent(user.Id, null));
            await session.SaveChangesAsync(ct);
        }

        // Materialize the async projection (no live daemon in tests).
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        using (var daemon = await store.BuildProjectionDaemonAsync("system"))
            await daemon.RebuildProjectionAsync<AuthAuditViewProjection>(TimeSpan.FromMinutes(2), ct);

        // Unfiltered → includes our login + password-change rows.
        var all = await Client.GetFromJsonAsync<List<AuditRowDto>>("/api/admin/audit?limit=500", JsonOptions, ct);
        Assert.NotNull(all);
        Assert.Contains(all!, r => r.EventType == AuditEvents.LoginSucceeded);
        Assert.Contains(all!, r => r.EventType == AuditEvents.AccountPasswordChanged);
        // the actor's identity is resolved at read time (joined from ApplicationUser)
        Assert.Contains(all!, r => !string.IsNullOrEmpty(r.User));

        // Category filter narrows to authentication only.
        var auth = await Client.GetFromJsonAsync<List<AuditRowDto>>(
            $"/api/admin/audit?category={AuditCategories.Authentication}", JsonOptions, ct);
        Assert.NotNull(auth);
        Assert.NotEmpty(auth!);
        Assert.All(auth!, r => Assert.Equal(AuditCategories.Authentication, r.Category));
    }

    [Fact]
    public async Task Get_hides_rows_older_than_the_visibility_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = Guid.NewGuid();

        // Store one recent + one 100-day-old row directly (the visibility window is
        // about Timestamp, which the projection can't backdate).
        using (var doc = GetTenantedDocumentSession())
        {
            doc.Store(new AuthAuditView
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Category = AuditCategories.Account,
                EventType = AuditEvents.AccountActivated,
                UserId = uid,
            });
            doc.Store(new AuthAuditView
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
                Category = AuditCategories.Account,
                EventType = AuditEvents.AccountDeactivated,
                UserId = uid,
            });
            await doc.SaveChangesAsync(ct);
        }

        // Default window is 90 days: the recent row shows, the 100-day-old one is hidden.
        var rows = await Client.GetFromJsonAsync<List<AuditRowDto>>("/api/admin/audit?limit=1000", JsonOptions, ct);
        Assert.NotNull(rows);
        Assert.Contains(rows!, r => r.EventType == AuditEvents.AccountActivated);
        Assert.DoesNotContain(rows!, r => r.EventType == AuditEvents.AccountDeactivated);
    }
}
