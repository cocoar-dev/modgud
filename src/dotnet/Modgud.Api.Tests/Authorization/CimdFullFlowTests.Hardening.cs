using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// DCR/CIMD-B5-01..03 — the CIMD resolver's caches and fetches:
/// B5-01 a cached document kept the token lifetimes of the context that first fetched it;
/// B5-02 a metadata host that sent its headers at once could hold the body back with no
/// deadline of our own (the HttpClient timeout ends with the headers);
/// B5-03 a failed unknown-kid JWKS refetch stored no cooldown, so every further made-up
/// kid fetched the client's host again during an outage.
/// </summary>
public partial class CimdFullFlowTests
{
    [Fact]
    public async Task A_cached_document_takes_the_current_token_lifetime()
    {
        await SeedAsync();
        RegisterCimdDocument();

        var (first, _) = await DriveCimdAuthCodeFlowAsync(_clientIdUrl, Scope, AllowedAudience);
        Assert.InRange(LifetimeMinutes(first), 14, 16);

        using (var scope = NewSystemTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>().PatchAsync(new UpdateRealmSettingsDto
            {
                Cimd = new UpdateCimdSettingsDto { AccessTokenLifetimeMinutes = 5 },
            }, TestContext.Current.CancellationToken);
        }

        // The consent from the first flow is remembered, so the second comes straight back.
        using var response = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, Scope, AllowedAudience, [AllowedAudience],
            expectConsent: false);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        Assert.InRange(LifetimeMinutes(doc.RootElement.GetProperty("access_token").GetString()!), 4, 6);
    }

    private static double LifetimeMinutes(string accessToken)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return (long.Parse(jwt.Payload["exp"].ToString()!) - long.Parse(jwt.Payload["iat"].ToString()!)) / 60.0;
    }
}
