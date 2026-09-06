using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// ADR 0022 (D6): every node resolves a realm's OIDC schemes from the
/// <see cref="LoginProvider"/> documents. These tests play "the other node" —
/// the one whose Wolverine handlers never ran for the change — by wiping the
/// in-memory registration and letting the materializer rebuild it from the
/// database, and pin the bounded-staleness contract of the request-path check.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LoginProviderSchemeMaterializerTests : IntegrationTestBase
{
    public LoginProviderSchemeMaterializerTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Provider_committed_elsewhere_is_materialised_from_the_database()
    {
        var provider = await CreateEnabledEntraProviderAsync();
        var scheme = DynamicOidcSchemeManager.SchemeNameFor(provider.Id);

        // "Node B": no handler ran for this provider (the document was written
        // directly), so only a read of the database can produce the scheme.
        var schemes = Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var materializer = Factory.Services.GetRequiredService<LoginProviderSchemeMaterializer>();
        await materializer.RefreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);

        var found = await LookupWithoutRealmAsync(schemes, scheme);
        Assert.NotNull(found);
        Assert.Equal(typeof(HostAwareOpenIdConnectHandler), found!.HandlerType);
    }

    [Fact]
    public async Task Request_path_resolves_the_scheme_for_the_current_realm()
    {
        var provider = await CreateEnabledEntraProviderAsync();
        var scheme = DynamicOidcSchemeManager.SchemeNameFor(provider.Id);

        var manager = Factory.Services.GetRequiredService<DynamicOidcSchemeManager>();
        await manager.UnregisterAsync(provider.Id);
        // Make the realm stale so the next request-path lookup re-reads it.
        var materializer = Factory.Services.GetRequiredService<LoginProviderSchemeMaterializer>();
        await materializer.ForgetAsync(TenantConstants.SystemTenantId);

        // The scheme provider consults the ambient realm exactly like a request
        // whose RealmMiddleware resolved the system tenant.
        var schemes = Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        AuthenticationScheme? found;
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            found = await schemes.GetSchemeAsync(scheme);

        Assert.NotNull(found);
    }

    [Fact]
    public async Task Provider_disabled_in_the_database_is_unregistered_on_refresh()
    {
        var provider = await CreateEnabledEntraProviderAsync();
        var scheme = DynamicOidcSchemeManager.SchemeNameFor(provider.Id);
        var materializer = Factory.Services.GetRequiredService<LoginProviderSchemeMaterializer>();
        var schemes = Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        await materializer.RefreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(await LookupWithoutRealmAsync(schemes, scheme));

        await using (var session = GetTenantedDocumentSession())
        {
            provider.Enabled = false;
            provider.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(provider);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await materializer.RefreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);

        Assert.Null(await LookupWithoutRealmAsync(schemes, scheme));
    }

    [Fact]
    public async Task EnsureFresh_within_the_interval_does_not_re_read_the_database()
    {
        var provider = await CreateEnabledEntraProviderAsync();
        var scheme = DynamicOidcSchemeManager.SchemeNameFor(provider.Id);
        var materializer = Factory.Services.GetRequiredService<LoginProviderSchemeMaterializer>();
        var manager = Factory.Services.GetRequiredService<DynamicOidcSchemeManager>();
        var schemes = Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        await materializer.RefreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(await LookupWithoutRealmAsync(schemes, scheme));

        // The database changes (another node disabled the provider) …
        await using (var session = GetTenantedDocumentSession())
        {
            provider.Enabled = false;
            provider.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(provider);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // … but inside the interval the request path does not re-read it.
        await materializer.EnsureFreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(await LookupWithoutRealmAsync(schemes, scheme));

        // A forced refresh (what the committing node's handler does) converges.
        await materializer.RefreshAsync(TenantConstants.SystemTenantId, TestContext.Current.CancellationToken);
        Assert.Null(await LookupWithoutRealmAsync(schemes, scheme));
        _ = manager;
    }

    // The realm-aware provider refreshes a STALE realm on GetSchemeAsync. Every
    // assertion here runs right after an explicit Refresh/Forget, well inside
    // the revalidation interval, so the lookup shows memory as it is.
    private static Task<AuthenticationScheme?> LookupWithoutRealmAsync(IAuthenticationSchemeProvider schemes, string name)
        => schemes.GetSchemeAsync(name);

    /// <summary>
    /// Writes the provider DOCUMENT straight into the realm store — no events,
    /// so none of this node's Wolverine handlers run. That is precisely the
    /// situation on the node that did not commit the change: the database has
    /// the provider, the process has never heard of it.
    /// </summary>
    private async Task<LoginProvider> CreateEnabledEntraProviderAsync(bool enabled = true)
    {
        var provider = new LoginProvider
        {
            Id = Guid.NewGuid(),
            Type = LoginProviderType.Oidc,
            Flavor = LoginProviderFlavor.EntraId,
            Slug = $"s{Guid.NewGuid():N}"[..12],
            DisplayName = $"Node-{Guid.NewGuid():N}"[..16],
            Enabled = enabled,
            ClientId = "client-id-1",
            Scopes = ["openid", "profile", "email"],
            UserUpdateScript = "(claims) => ({ email: claims.email })",
            AllowLinking = true,
            FlavorData = JsonDocument.Parse("""{"TenantId": "11111111-2222-3333-4444-555555555555"}"""),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await using var session = GetTenantedDocumentSession();
        session.Store(provider);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return provider;
    }
}
