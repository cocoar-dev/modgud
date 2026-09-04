using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Domain.Common;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1d drift-guard: the manifest path must produce the SAME state as the canonical
/// admin operation for the same logical input. An OAuth client is built two ways — via
/// <see cref="OAuthAdminService.CreateClientAsync"/> with an explicit DTO (what the admin
/// UI/API submits) in realm A, and via a manifest import in realm B — and the projected
/// client shape is asserted identical. Both go through the same canonical service, so a
/// mismatch can only mean the applier's manifest→DTO mapping has drifted.
/// </summary>
public class RealmManifestParityTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Client_built_via_admin_service_and_via_manifest_have_identical_state()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slugA = "parity-admin";
        const string slugB = "parity-manifest";

        // Realm A: import just the realm + app, then create the client via the canonical
        // OAuthAdminService with an explicit DTO (the admin-API path).
        var importA = await applier.ImportNewRealmAsync(BaseManifest(slugA), ct);
        Assert.False(importA.IsError, importA.IsError ? importA.FirstError.Description : string.Empty);
        await InTenantAsync(factory, slugA, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var appId = (await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "parity-app", ct)).Id;
            var oauth = sp.GetRequiredService<OAuthAdminService>();
            var created = await oauth.CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = "parity-web",
                DisplayName = "Parity Web",
                ClientType = "confidential",
                RedirectUris = ["https://parity.test/cb"],
                Scopes = ["openid"],
                AllowedGrantTypes = ["authorization_code", "refresh_token"],
                AllowedCorsOrigins = ["https://parity.test"],
                Enabled = true,
                RequireConsent = false,
                AccessTokenType = AccessTokenType.Jwt,
                RequirePushedAuthorizationRequests = true,
                RequireDpop = true,
                Capabilities = ["cap:trusted-forwarder"],
                BackChannelLogoutUri = "https://parity.test/oidc/backchannel-logout",
                BackChannelLogoutSessionRequired = false,
                AccessTokenLifetime = 300,
                Claims = [new OAuthClientClaimDto { Type = "tenant", Value = "parity" }],
                ClientClaimsPrefix = "client_",
                AlwaysSendClientClaims = true,
                AppIds = [new ShortGuid(appId).ToString()],
            }, ct);
            Assert.False(created.IsError, created.IsError ? created.FirstError.Description : string.Empty);
        });

        // Realm B: the SAME client described in the manifest (the applier path).
        var manifestB = BaseManifest(slugB) with
        {
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "parity-web",
                    DisplayName = "Parity Web",
                    ClientType = "confidential",
                    RedirectUris = ["https://parity.test/cb"],
                    Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    AllowedCorsOrigins = ["https://parity.test"],
                    AccessTokenType = "Jwt",
                    RequirePushedAuthorizationRequests = true,
                    RequireDpop = true,
                    Capabilities = ["cap:trusted-forwarder"],
                    BackChannelLogoutUri = new Optional<string?>("https://parity.test/oidc/backchannel-logout"),
                    BackChannelLogoutSessionRequired = false,
                    AccessTokenLifetime = 300,
                    Claims = [new RealmManifestClientClaim("tenant", "parity")],
                    ClientClaimsPrefix = "client_",
                    AlwaysSendClientClaims = true,
                    Apps = ["parity-app"],
                },
            ],
        };
        var importB = await applier.ImportNewRealmAsync(manifestB, ct);
        Assert.False(importB.IsError, importB.IsError ? importB.FirstError.Description : string.Empty);

        var shapeA = await GetClientShapeAsync(factory, slugA, "parity-web", ct);
        var shapeB = await GetClientShapeAsync(factory, slugB, "parity-web", ct);
        Assert.Equal(shapeA, shapeB);
    }

    /// <summary>A realm-independent, order-stable projection of a client's externally
    /// meaningful state. App links are normalised to slugs (the ids differ per realm).</summary>
    private sealed record ClientShape(
        string ClientType,
        string ConsentType,
        string RedirectUris,
        string PostLogoutRedirectUris,
        string AllowedGrantTypes,
        string Permissions,
        string CorsOrigins,
        bool Enabled,
        bool RequireConsent,
        AccessTokenType AccessTokenType,
        bool RequirePushedAuthorizationRequests,
        bool RequireDpop,
        bool RequireDpopNonce,
        int? AccessTokenLifetime,
        string Claims,
        string? ClientClaimsPrefix,
        bool AlwaysSendClientClaims,
        string AppSlugs,
        string Capabilities,
        string? BackChannelLogoutUri,
        bool BackChannelLogoutSessionRequired);

    private static async Task<ClientShape> GetClientShapeAsync(
        ColdStartWebApplicationFactory factory, string slug, string clientId, CancellationToken ct)
    {
        ClientShape shape = null!;
        await InTenantAsync(factory, slug, async sp =>
        {
            var oauth = sp.GetRequiredService<OAuthAdminService>();
            var client = (await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct))
                .Items.Single(c => c.ClientId == clientId);

            // Resolve the app links to slugs so the comparison is realm-independent.
            var session = sp.GetRequiredService<IDocumentSession>();
            var slugs = new List<string>();
            foreach (var appId in client.AppIds)
            {
                var app = await session.LoadAsync<App>(new ShortGuid(appId).Guid, ct);
                if (app is not null) slugs.Add(app.Slug);
            }

            shape = new ClientShape(
                client.ClientType,
                client.ConsentType,
                Join(client.RedirectUris),
                Join(client.PostLogoutRedirectUris),
                Join(client.AllowedGrantTypes),
                Join(client.Permissions),
                Join(client.AllowedCorsOrigins),
                client.Enabled,
                client.RequireConsent,
                client.AccessTokenType,
                client.RequirePushedAuthorizationRequests,
                client.RequireDpop,
                client.RequireDpopNonce,
                client.AccessTokenLifetime,
                Join(client.Claims.Select(c => $"{c.Type}={c.Value}")),
                client.ClientClaimsPrefix,
                client.AlwaysSendClientClaims,
                Join(slugs),
                Join(client.Capabilities),
                client.BackChannelLogoutUri,
                client.BackChannelLogoutSessionRequired);
        });
        return shape;
    }

    private static string Join(IEnumerable<string> values) => string.Join(",", values.OrderBy(v => v, StringComparer.Ordinal));

    private static RealmManifest BaseManifest(string slug) => new()
    {
        Realm = new CreateRealmDto
        {
            Slug = slug,
            DisplayName = slug,
            Domains = [$"{slug}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
        },
        Apps =
        [
            new RealmManifestApp
            {
                Slug = "parity-app",
                DisplayName = "Parity App",
                Permissions = [new RealmManifestPermission("parity", "read")],
            },
        ],
    };

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
