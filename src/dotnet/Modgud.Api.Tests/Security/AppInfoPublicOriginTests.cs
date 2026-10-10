using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// The admin UI builds an App's related-origins file (<c>/.well-known/webauthn</c>) from the
/// realm's declared public origin (ADR 0023), not from the address the admin happens to
/// have open. <c>/api/app-info</c> reports it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AppInfoPublicOriginTests : IntegrationTestBase
{
    public AppInfoPublicOriginTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task App_info_reports_the_realms_public_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        var realm = await Factory.Services.GetRequiredService<IRealmProvisioningService>()
            .GetRealmBySlugAsync("system", ct);
        Assert.NotNull(realm);

        var body = await Factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/app-info", ct);

        Assert.Equal(RealmPublicOrigin.Resolve(realm!), body.GetProperty("PublicOrigin").GetString());
    }
}
