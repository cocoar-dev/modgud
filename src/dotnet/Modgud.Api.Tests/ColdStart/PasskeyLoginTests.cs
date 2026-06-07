using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 3 finding: a passkey ceremony needs the realm's PrimaryDomain as the
/// WebAuthn relying-party ID. When a realm has none (a migration gap / manual
/// edit the create/update/boot invariants normally prevent), building the RP
/// throws — and the passkey endpoints used to surface that as an opaque 500.
/// It must now fail gracefully with a clear, actionable response.
/// </summary>
public class PasskeyLoginTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Passkey_login_options_fails_gracefully_when_the_realm_has_no_primary_domain()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // Force the no-PrimaryDomain state the invariants normally prevent.
        var globalStore = host.Services.GetRequiredService<IGlobalStore>();
        await using (var gs = globalStore.LightweightSession())
        {
            var realm = await gs.Query<Realm>().Where(r => r.Slug == "system").FirstAsync(ct);
            realm.PrimaryDomain = "";
            gs.Store(realm);
            await gs.SaveChangesAsync(ct);
        }
        host.Services.GetRequiredService<IRealmCache>().Invalidate();

        var client = host.Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/account/passkey/login-options", new { }, ct);

        // GREEN: a clear, non-500 response. (RED without the fix: an opaque 500
        // from the unhandled RelyingPartyUnavailableException.)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("Passkey.Unavailable", body);
    }
}
