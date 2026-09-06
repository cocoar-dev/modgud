using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Infrastructure.Persistence.Tenancy;
using Wolverine;

namespace Modgud.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class DynamicOidcSchemeManagerTests : IntegrationTestBase
{
    public DynamicOidcSchemeManagerTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RegisterAsync_EnabledConfig_MakesSchemeAvailable()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);

        var expectedName = DynamicOidcSchemeManager.SchemeNameFor(config.Id);
        var scheme = await schemeProvider.GetSchemeAsync(expectedName);
        Assert.NotNull(scheme);
        // Host-aware subclass: adds the per-tenant callback tiebreaker over the
        // framework handler. See HostAwareOpenIdConnectHandler.
        Assert.Equal(typeof(HostAwareOpenIdConnectHandler), scheme!.HandlerType);
        Assert.Equal(config.DisplayName, scheme.DisplayName);
    }

    [Fact]
    public async Task RegisterAsync_DisabledConfig_IsUnregistered()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);
        var name = DynamicOidcSchemeManager.SchemeNameFor(config.Id);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(name));

        // ADR 0022: the database is the source of truth on every node. Disable
        // the provider there; a refresh (what the committing node's handler and
        // every other node's request path do) drops the scheme.
        await using (var session = GetTenantedDocumentSession())
        {
            session.Events.Append(config.Id, new LoginProviderDisabledEvent(config.Id, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Factory.Services.GetRequiredService<LoginProviderSchemeMaterializer>()
            .RefreshAsync("system", TestContext.Current.CancellationToken);

        Assert.Null(await schemeProvider.GetSchemeAsync(name));
    }

    [Fact]
    public async Task RegisterAsync_EmptyClientId_Skipped()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: false);

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        await manager.RegisterAsync(config);

        // Incomplete config must NOT produce a reachable scheme (login would 500)
        var name = DynamicOidcSchemeManager.SchemeNameFor(config.Id);
        Assert.Null(await schemeProvider.GetSchemeAsync(name));
    }

    [Fact]
    public async Task RegisterAsync_InternalType_DoesNotRegisterScheme()
    {
        // Phase 2: Type-discriminator gate. Internal-typed providers must
        // never enter the OIDC scheme machinery, even if the (defensive)
        // event handlers try to feed them in. No exception, no scheme.
        var config = new LoginProvider
        {
            Id = Guid.NewGuid(),
            Type = LoginProviderType.Internal,
            Flavor = LoginProviderFlavor.Internal,
            DisplayName = "Internal",
            Enabled = true,
            IsBuiltIn = true,
        };

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        await manager.RegisterAsync(config); // must not throw

        var name = DynamicOidcSchemeManager.SchemeNameFor(config.Id);
        Assert.Null(await schemeProvider.GetSchemeAsync(name));
    }

    [Theory]
    [InlineData(LoginProviderType.Saml)]
    [InlineData(LoginProviderType.Ldap)]
    [InlineData(LoginProviderType.Kerberos)]
    public async Task RegisterAsync_NonOidcTypes_AreSkipped(LoginProviderType type)
    {
        // SAML has its own DynamicSamlSchemeManager; LDAP/Kerberos remain
        // unsupported. None of them may enter the OIDC scheme machinery.
        var config = new LoginProvider
        {
            Id = Guid.NewGuid(),
            Type = type,
            Flavor = "doesnt-matter",
            DisplayName = $"{type}-test",
            Enabled = true,
            ClientId = "x",
        };

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        await manager.RegisterAsync(config);

        var name = DynamicOidcSchemeManager.SchemeNameFor(config.Id);
        Assert.Null(await schemeProvider.GetSchemeAsync(name));
    }

    [Fact]
    public async Task RegisterAsync_Twice_UpdatesOptions()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);

        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var optionsMonitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);
        var first = optionsMonitor.Get(DynamicOidcSchemeManager.SchemeNameFor(config.Id));
        Assert.Equal("client-id-1", first.ClientId);

        config.ClientId = "client-id-2";
        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);
        var second = optionsMonitor.Get(DynamicOidcSchemeManager.SchemeNameFor(config.Id));
        Assert.Equal("client-id-2", second.ClientId);
    }

    [Fact]
    public async Task GetRegisteredExternalSchemes_ExcludesPlaceholder()
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();

        var schemes = await manager.GetRegisteredExternalSchemesAsync();

        Assert.DoesNotContain(schemes, s => s.Name.EndsWith("placeholder", StringComparison.Ordinal));
        Assert.All(schemes, s => Assert.StartsWith(DynamicOidcSchemeManager.SchemeNamePrefix, s.Name));
    }

    [Fact]
    public async Task ExternalLoginsEndpoint_ListsEnabledConfigsOnly()
    {
        var enabled = await CreateEntraConfigAsync(enabled: true, withClientId: true, displayName: "Acme Entra");
        var disabled = await CreateEntraConfigAsync(enabled: false, withClientId: true, displayName: "Staging Entra");

        // Anonymous allowed per endpoint mapping
        using var anonClient = Factory.CreateDefaultClient();
        var response = await anonClient.GetAsync("/api/account/external-logins", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var items = await response.Content.ReadFromJsonAsync<ExternalAuthEndpoints.ExternalLoginDto[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Contains(items, x => x.Id == enabled.Id);
        Assert.DoesNotContain(items, x => x.Id == disabled.Id);
    }

    private async Task<LoginProvider> CreateEntraConfigAsync(
        bool enabled,
        bool withClientId,
        string? displayName = null)
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var flavorData = JsonDocument.Parse(
            """{"TenantId": "11111111-2222-3333-4444-555555555555"}""");

        var name = displayName ?? $"Test-{Guid.NewGuid():N}"[..16];
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.EntraId,
            DisplayName: name,
            Slug: $"s{Guid.NewGuid():N}"[..12],
            FlavorData: flavorData));
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");

        var id = result.Value.Id;

        // Patch the projection directly for test convenience — fastest way to
        // get a fully-populated, enabled provider without wiring the secret-
        // rotation flow.
        session.Events.Append(id,
            new LoginProviderUpdatedEvent(
                Id: id,
                DisplayName: name,
                Description: null,
                ClientId: withClientId ? "client-id-1" : string.Empty,
                Scopes: ["openid", "profile", "email"],
                UserUpdateScript: "(claims) => ({ email: claims.email })",
                StoreRawClaims: true,
                RawClaimsRetentionDays: null,
                AutoCreateUsers: false,
                AllowLinking: true,
                TrustForEmailLink: false,
                AllowedEmailDomains: null,
                IconName: "microsoft",
                ButtonColorHex: null,
                FlavorData: flavorData,
                UpdatedAt: DateTimeOffset.UtcNow));
        if (enabled)
        {
            session.Events.Append(id, new LoginProviderEnabledEvent(id, DateTimeOffset.UtcNow));
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (await session.LoadAsync<LoginProvider>(id, TestContext.Current.CancellationToken))!;
    }
}
