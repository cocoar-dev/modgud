using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.AuthLog;
using Marten;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Auth-log entries are persisted to the system DB and attributed to a realm.
/// The read endpoint reaches that system DB and returns the realm field; the
/// control-plane (system) realm — which the default test admin runs in — sees
/// the full cross-realm log. The per-realm exclusion of the filter itself is
/// unit-tested deterministically in
/// <c>AuthLogAttributionTests.Scope_TenantRealm_SeesOnlyOwnRealm</c> (a tenant
/// realm-admin authenticated request needs full multi-realm host routing + a
/// per-tenant login, out of proportion for this trivial Where).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthLogTenantVisibilityTests : IntegrationTestBase
{
    public AuthLogTenantVisibilityTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Read_AsControlPlaneAdmin_ReturnsAllRealms_WithRealmField()
    {
        // Entries live in the system DB regardless of which realm emitted them.
        await using (var write = GetTenantedDocumentSession("system"))
        {
            write.Store(new AuthLogDocument { Timestamp = DateTimeOffset.UtcNow, Level = "Info", Message = "sk-vis-system", Realm = "system" });
            write.Store(new AuthLogDocument { Timestamp = DateTimeOffset.UtcNow, Level = "Info", Message = "sk-vis-acme", Realm = "acme" });
            write.Store(new AuthLogDocument { Timestamp = DateTimeOffset.UtcNow, Level = "Info", Message = "sk-vis-unattributed", Realm = null });
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The default Client is a realm-admin in the system (control-plane) realm.
        var entries = await Client.GetFromJsonAsync<List<AuthLogDocument>>(
            "/api/admin/auth-log?limit=500", TestContext.Current.CancellationToken);

        Assert.NotNull(entries);
        var byMessage = entries!
            .Where(e => e.Message.StartsWith("sk-vis-"))
            .ToDictionary(e => e.Message, e => e.Realm);

        // Control-plane sees its own realm AND other realms AND unattributed events.
        Assert.Equal("system", byMessage["sk-vis-system"]);
        Assert.Equal("acme", byMessage["sk-vis-acme"]);
        Assert.True(byMessage.ContainsKey("sk-vis-unattributed"));
        Assert.Null(byMessage["sk-vis-unattributed"]);
    }
}
