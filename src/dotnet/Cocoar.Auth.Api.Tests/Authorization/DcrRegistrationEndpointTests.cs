using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Application.Dcr;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.DTOs.RealmSettings;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.RealmSettings;
using Cocoar.Auth.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Api.Tests.Authorization;

/// <summary>
/// End-to-end pinning of the public <c>/connect/register</c> endpoint
/// and the discovery-document advertisement. Exercises the full
/// pipeline (RealmMiddleware → settings load → validator →
/// OAuthAdminService write → 201/RFC-7591 response) against the
/// Postgres testcontainer.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DcrRegistrationEndpointTests : IntegrationTestBase
{
    public DcrRegistrationEndpointTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Returns_404_when_realm_dcr_disabled()
    {
        // Default realm state: DCR off. The endpoint should look like
        // it doesn't exist.
        var client = Factory.CreateClient();
        var body = JsonContent.Create(new
        {
            client_name = "Doesn't matter",
            redirect_uris = new[] { "https://example.com/cb" },
        });

        var resp = await client.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Discovery_document_omits_registration_endpoint_when_dcr_disabled()
    {
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);
        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(bodyText);

        Assert.False(doc.RootElement.TryGetProperty("registration_endpoint", out _),
            "registration_endpoint must NOT appear in discovery when DCR is disabled");
    }

    [Fact]
    public async Task Discovery_document_includes_registration_endpoint_when_dcr_enabled()
    {
        await EnableDcrAsync();
        var client = Factory.CreateClient();

        var resp = await client.GetAsync("/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);
        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(bodyText);

        Assert.True(doc.RootElement.TryGetProperty("registration_endpoint", out var endpoint));
        var endpointStr = endpoint.GetString()!;
        Assert.EndsWith("/connect/register", endpointStr);
    }

    [Fact]
    public async Task Returns_201_with_RFC7591_shape_on_happy_path()
    {
        await EnableDcrAsync();
        var client = Factory.CreateClient();

        var body = JsonContent.Create(new
        {
            client_name = "Integration Test Client",
            redirect_uris = new[] { "https://client.example.com/callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
        });

        var resp = await client.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);
        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created, got {(int)resp.StatusCode}: {bodyText}");

        using var doc = JsonDocument.Parse(bodyText);
        var clientId = doc.RootElement.GetProperty("client_id").GetString()!;
        Assert.StartsWith("dcr-", clientId);
        Assert.True(doc.RootElement.GetProperty("client_id_issued_at").GetInt64() > 0);
        Assert.Equal("none", doc.RootElement.GetProperty("token_endpoint_auth_method").GetString());

        var grants = doc.RootElement.GetProperty("grant_types").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains("authorization_code", grants);
        Assert.Contains("refresh_token", grants);

        var redirects = doc.RootElement.GetProperty("redirect_uris").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "https://client.example.com/callback" }, redirects);
    }

    [Fact]
    public async Task Returns_400_invalid_redirect_uri_for_non_loopback_http()
    {
        await EnableDcrAsync();
        var client = Factory.CreateClient();

        var body = JsonContent.Create(new
        {
            client_name = "Bad URI Client",
            redirect_uris = new[] { "http://attacker.example.com/cb" },
        });

        var resp = await client.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(DcrErrorCodes.InvalidRedirectUri, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Registered_client_carries_full_DCR_property_set_in_storage()
    {
        await EnableDcrAsync();
        var http = Factory.CreateClient();

        var body = JsonContent.Create(new
        {
            client_name = "Property Set Probe",
            redirect_uris = new[] { "https://probe.example.com/callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
        });
        var resp = await http.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(bodyText);
        var clientId = doc.RootElement.GetProperty("client_id").GetString()!;

        // Inspect storage via the tenanted admin service — proves the
        // DCR properties land on the persisted projection state, not
        // just on the response wire shape.
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()
            .HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { ["TenantId"] = "system" },
            };
        var oauthAdmin = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();

        var listing = await oauthAdmin.GetClientsAsync(
            new PaginationRequest { Page = 1, PageSize = 100 },
            TestContext.Current.CancellationToken);
        var stored = listing.Items.SingleOrDefault(c => c.ClientId == clientId);
        Assert.NotNull(stored);
        Assert.True(stored.IsDynamicallyRegistered);
        Assert.NotNull(stored.DcrRegisteredAt);
        Assert.NotNull(stored.DcrRegisteredFromIp);
        Assert.Equal(stored.DcrRegisteredAt, stored.DcrLastUsedAt);
        // The validator should have forced public+explicit-consent shape.
        Assert.Equal(OAuthClientTypes.Public, stored.ClientType);
        Assert.Equal(OAuthConsentTypes.Explicit, stored.ConsentType);
        Assert.False(stored.AllowRememberConsent);
    }

    [Fact]
    public async Task Returns_400_when_client_name_matches_realm_reserved_name()
    {
        await EnableDcrAsync(reservedNames: new[] { "Cocoar" });
        var client = Factory.CreateClient();

        var body = JsonContent.Create(new
        {
            client_name = "Cocoar Helper Pro",
            redirect_uris = new[] { "https://client.example.com/cb" },
        });

        var resp = await client.PostAsync("/connect/register", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var bodyText = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(DcrErrorCodes.InvalidClientMetadata, doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("reserved", doc.RootElement.GetProperty("error_description").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Flips RealmSettings.Dcr.Enabled to true for the system tenant
    /// (which is what the test pipeline uses).
    /// </summary>
    private async Task EnableDcrAsync(string[]? reservedNames = null)
    {
        using var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()
            .HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                Items = { ["TenantId"] = "system" },
            };

        var settingsService = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settingsService.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto
            {
                Enabled = true,
                ReservedNames = reservedNames,
            },
        }, TestContext.Current.CancellationToken);
    }
}
