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

    [Fact]
    public async Task A_metadata_body_held_back_after_the_headers_does_not_hold_the_request()
    {
        await SeedAsync();
        RegisterCimdDocument();
        Factory.CimdBodyDelays[_clientIdUrl] = TimeSpan.FromSeconds(40);

        var watch = Stopwatch.StartNew();
        using var authorize = await Factory.CreateClient().GetAsync(
            $"/connect/authorize?response_type=code&client_id={Uri.EscapeDataString(_clientIdUrl)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope=openid&code_challenge=abc&code_challenge_method=S256",
            TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the authorize request waited {watch.Elapsed} for the body");
        Assert.False(authorize.IsSuccessStatusCode && authorize.Headers.Location?.Query.Contains("code=") == true);
    }

    [Fact]
    public async Task A_failed_unknown_kid_refetch_cools_down_and_keeps_the_known_keys()
    {
        await SeedAsync();
        var ct = TestContext.Current.CancellationToken;
        using var keys = new TestJwks("b503-1");
        var jwksUri = $"https://cimd-app.test/oauth/{Guid.NewGuid():N}/jwks.json";
        Factory.CimdDocuments[jwksUri] = keys.PublicJwks;
        Factory.CimdDocuments[_clientIdUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = _clientIdUrl,
            ["client_name"] = "B5-03",
            ["redirect_uris"] = new[] { RedirectUri },
            ["token_endpoint_auth_method"] = "private_key_jwt",
            ["token_endpoint_auth_signing_alg"] = "RS256",
            ["jwks_uri"] = jwksUri,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
        });
        var issuer = await IssuerAsync();
        var scope = $"openid offline_access {ScopeName}";

        var ok = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            clientAssertion: keys.MintAssertion(_clientIdUrl, issuer));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync(ct));

        // The client's key host goes down.
        Factory.CimdDocuments.TryRemove(jwksUri, out _);
        var before = Factory.CimdFetchCounts[jwksUri];
        using var madeUpA = new TestJwks("b503-made-up-a");
        using var madeUpB = new TestJwks("b503-made-up-b");
        await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            expectConsent: false, clientAssertion: madeUpA.MintAssertion(_clientIdUrl, issuer));
        await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            expectConsent: false, clientAssertion: madeUpB.MintAssertion(_clientIdUrl, issuer));

        // One refetch for the first unknown kid, then the cooldown holds.
        Assert.Equal(before + 1, Factory.CimdFetchCounts[jwksUri]);

        // The keys the client still uses keep working meanwhile.
        var still = await DriveCimdFlowThroughToTokenAsync(_clientIdUrl, scope, AllowedAudience, [AllowedAudience],
            expectConsent: false, clientAssertion: keys.MintAssertion(_clientIdUrl, issuer));
        Assert.True(still.IsSuccessStatusCode, await still.Content.ReadAsStringAsync(ct));
    }

    private static double LifetimeMinutes(string accessToken)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return (long.Parse(jwt.Payload["exp"].ToString()!) - long.Parse(jwt.Payload["iat"].ToString()!)) / 60.0;
    }
}
