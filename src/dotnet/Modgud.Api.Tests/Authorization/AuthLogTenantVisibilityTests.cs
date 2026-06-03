using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Streamless security-store entries live in the system DB and are attributed to a
/// realm. The read endpoint (<c>GET /api/admin/auth-log</c>) reaches that system DB
/// and returns the realm field; the control-plane (system) realm — which the default
/// test admin runs in — sees the full cross-realm log INCLUDING control-plane-only
/// (<c>PlatformOnly</c>) operational rows. The per-realm + tenant-visibility exclusion
/// of the filter itself is unit-tested deterministically in
/// <c>AuthLogAttributionTests</c> (a tenant realm-admin authenticated request needs
/// full multi-realm host routing + a per-tenant login, out of proportion here).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthLogTenantVisibilityTests : IntegrationTestBase
{
    public AuthLogTenantVisibilityTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record Row(string Message, string? Realm);

    [Fact]
    public async Task Read_AsControlPlaneAdmin_ReturnsAllRealms_IncludingPlatformOnly()
    {
        var ct = TestContext.Current.CancellationToken;

        // Entries live in the system DB regardless of which realm emitted them.
        await using (var write = GetTenantedDocumentSession("system"))
        {
            write.Store(new SecurityAuditEntry { Timestamp = DateTimeOffset.UtcNow, Level = "Info", EventType = AuditEvents.LoginFailedUnknownUser, Message = "sk-vis-system", Realm = "system", PlatformOnly = false });
            write.Store(new SecurityAuditEntry { Timestamp = DateTimeOffset.UtcNow, Level = "Info", EventType = AuditEvents.LoginFailedUnknownUser, Message = "sk-vis-acme", Realm = "acme", PlatformOnly = false });
            write.Store(new SecurityAuditEntry { Timestamp = DateTimeOffset.UtcNow, Level = "Info", EventType = AuditEvents.LoginFailedUnknownUser, Message = "sk-vis-unattributed", Realm = null, PlatformOnly = false });
            // A control-plane-only operational row — visible to the control-plane reader.
            write.Store(new SecurityAuditEntry { Timestamp = DateTimeOffset.UtcNow, Level = "Warning", EventType = AuditEvents.RecoveryCliInvoked, Message = "sk-vis-platform", Realm = "acme", PlatformOnly = true });
            await write.SaveChangesAsync(ct);
        }

        // The default Client is a realm-admin in the system (control-plane) realm.
        var entries = await Client.GetFromJsonAsync<List<Row>>(
            "/api/admin/auth-log?limit=500", ct);

        Assert.NotNull(entries);
        var byMessage = entries!
            .Where(e => e.Message.StartsWith("sk-vis-"))
            .ToDictionary(e => e.Message, e => e.Realm);

        // Control-plane sees its own realm AND other realms AND unattributed events AND
        // control-plane-only operational rows.
        Assert.Equal("system", byMessage["sk-vis-system"]);
        Assert.Equal("acme", byMessage["sk-vis-acme"]);
        Assert.True(byMessage.ContainsKey("sk-vis-unattributed"));
        Assert.Null(byMessage["sk-vis-unattributed"]);
        Assert.True(byMessage.ContainsKey("sk-vis-platform")); // PlatformOnly row visible to control-plane
    }
}
