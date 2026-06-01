using System.Security.Claims;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Gdpr;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Federation v1 — Phase 1 (claims capture, source-tagged persistence, scrub).
/// Pins: every successful external login refreshes the per-user
/// <see cref="ExternalClaimsStore"/> for the current provider only (delete+rewrite),
/// tagged <c>provider:&lt;slug&gt;</c>; and both deletion paths (GDPR erase + admin
/// delete) scrub the store. No authorization behavior is wired yet (phases 2-4).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FederationV1Phase1Tests : IntegrationTestBase
{
    public FederationV1Phase1Tests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Issuer = "https://idp.federation.test/v2.0";

    [Fact]
    public async Task Login_Persists_Provider_Tagged_Claims_Including_Groups()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateEnabledOidcProviderAsync(autoCreate: true);

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
        var external = BuildPrincipal("sub-cap-1", "cap1@acme.com", ["IT", "Admins"]);

        var result = await processor.ProcessAsync(external, config.Id, ct);
        Assert.True(result.Succeeded);

        await using var read = GetTenantedSession();
        var store = await read.LoadAsync<ExternalClaimsStore>(result.UserId!.Value, ct);
        Assert.NotNull(store);

        var source = $"provider:{config.Slug}";
        Assert.All(store!.Claims, e => Assert.Equal(source, e.Source));

        var groups = store.Claims.Where(e => e.Type == "groups").Select(e => e.Value).ToList();
        Assert.Contains("IT", groups);
        Assert.Contains("Admins", groups);
        Assert.Contains(store.Claims, e => e.Type == "email" && e.Value == "cap1@acme.com");
    }

    [Fact]
    public async Task ReLogin_Replaces_The_Providers_Entries_Not_Appends()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateEnabledOidcProviderAsync(autoCreate: true);

        // First login (JIT) with group A.
        Guid userId;
        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var r1 = await processor.ProcessAsync(
                BuildPrincipal("sub-rerun", "rerun@acme.com", ["GroupA"]), config.Id, ct);
            Assert.True(r1.Succeeded);
            userId = r1.UserId!.Value;
        }

        // Second login (returning) with groups B + C — must REPLACE A.
        using (var scope = Factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
            var r2 = await processor.ProcessAsync(
                BuildPrincipal("sub-rerun", "rerun@acme.com", ["GroupB", "GroupC"]), config.Id, ct);
            Assert.True(r2.Succeeded);
            Assert.Equal(userId, r2.UserId);
        }

        await using var read = GetTenantedSession();
        var store = await read.LoadAsync<ExternalClaimsStore>(userId, ct);
        Assert.NotNull(store);
        var groups = store!.Claims.Where(e => e.Type == "groups").Select(e => e.Value).ToList();
        Assert.DoesNotContain("GroupA", groups);
        Assert.Contains("GroupB", groups);
        Assert.Contains("GroupC", groups);
    }

    [Fact]
    public async Task GdprErase_Scrubs_The_Claims_Store()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Claims", lastname: "Gdpr", acronym: "CG", email: "claims-gdpr@test.com");

        await SeedClaimsStoreAsync(user.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var result = await gdpr.PermanentlyEraseAsync(
                user.Id, adminUserId: null, reason: "test-erase", ct);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        }

        await using var read = GetTenantedSession();
        Assert.Null(await read.LoadAsync<ExternalClaimsStore>(user.Id, ct));
    }

    [Fact]
    public async Task AdminDelete_Scrubs_The_Claims_Store()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Claims", lastname: "Del", acronym: "CD", email: "claims-del@test.com");

        await SeedClaimsStoreAsync(user.Id);

        // Admin "delete" is now a reversible recycle-bin move — the claims
        // snapshot is KEPT so a restore stays clean (live access is revoked, so
        // a stale snapshot is harmless in the meantime).
        var binResponse = await Client.DeleteAsync($"/api/user/{new ShortGuid(user.Id)}", ct);
        binResponse.EnsureSuccessStatusCode();

        await using (var afterBin = GetTenantedSession())
            Assert.NotNull(await afterBin.LoadAsync<ExternalClaimsStore>(user.Id, ct));

        // Permanent erase (ForceDelete / empty-bin) is what scrubs the snapshot —
        // externally-derived authz can never outlive the user.
        var eraseResponse = await Client.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/admin/users/{new ShortGuid(user.Id)}/permanent")
        {
            Content = JsonContent.Create(new { Reason = "test-erase" }, options: JsonOptions),
        }, ct);
        eraseResponse.EnsureSuccessStatusCode();

        await using var read = GetTenantedSession();
        Assert.Null(await read.LoadAsync<ExternalClaimsStore>(user.Id, ct));
    }

    private async Task SeedClaimsStoreAsync(Guid userId)
    {
        await using var seed = GetTenantedDocumentSession();
        seed.Store(new ExternalClaimsStore
        {
            Id = userId,
            Claims =
            [
                new ClaimEntry("provider:seed", "email", "claims-seed@test.com", DateTimeOffset.UtcNow),
                new ClaimEntry("provider:seed", "groups", "Finance", DateTimeOffset.UtcNow),
            ],
        });
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<LoginProvider> CreateEnabledOidcProviderAsync(bool autoCreate)
    {
        var id = Guid.NewGuid();
        var slug = $"fed{Guid.NewGuid():N}"[..12];
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<LoginProvider>(id, new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Oidc,
            Flavor: LoginProviderFlavor.GenericOidc,
            Slug: slug,
            DisplayName: $"Fed_{Guid.NewGuid():N}"[..12],
            Description: null,
            IsBuiltIn: false,
            Enabled: true,
            ClientId: "client",
            ClientSecretEncrypted: null,
            Scopes: ["openid", "profile", "email"],
            UserUpdateScript: "(claims) => ({ email: claims.email })",
            StoreRawClaims: false,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: autoCreate,
            AllowLinking: true,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: null,
            ButtonColorHex: null,
            FlavorData: null,
            CreatedAt: DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<LoginProvider>(id, TestContext.Current.CancellationToken))!;
    }

    private static ClaimsPrincipal BuildPrincipal(string subject, string email, IReadOnlyList<string> groups)
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("sub", subject));
        identity.AddClaim(new Claim("email", email));
        foreach (var g in groups)
            identity.AddClaim(new Claim("groups", g));
        return new ClaimsPrincipal(identity);
    }
}
