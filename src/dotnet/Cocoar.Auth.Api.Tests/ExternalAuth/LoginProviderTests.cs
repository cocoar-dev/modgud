using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Authentication.Api.Admin.LoginProviders.Commands;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Domain.LoginProviders.Events;
using Wolverine;

namespace Cocoar.Auth.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class LoginProviderTests : IntegrationTestBase
{
    public LoginProviderTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Create_PersistsLoginProvider_WithFlavorDefaults()
    {
        // LoginProvider can be created via Wolverine-style command handler,
        // the event is persisted, the inline projection materializes the
        // document with flavor-derived defaults.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"TenantId": "11111111-2222-3333-4444-555555555555"}""");
        var command = new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.EntraId,
            DisplayName: "Acme Entra",
            FlavorData: flavorData);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(command);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var config = result.Value;

        Assert.NotEqual(Guid.Empty, config.Id);
        Assert.Equal(LoginProviderType.Oidc, config.Type);
        Assert.Equal(LoginProviderFlavor.EntraId, config.Flavor);
        Assert.Equal("Acme Entra", config.DisplayName);
        Assert.False(config.IsBuiltIn);
        Assert.False(config.Enabled, "New config should be disabled until admin opts in");
        Assert.True(config.StoreRawClaims, "Entra should default to storing raw claims");
        Assert.Contains("openid", config.Scopes);
        Assert.False(string.IsNullOrWhiteSpace(config.UserUpdateScript));
        Assert.False(config.AutoCreateUsers, "Auto-create defaults off");
        Assert.True(config.AllowLinking, "Linking allowed by default");
        Assert.False(config.TrustForEmailLink, "Trust-for-email defaults off (impersonation hardening)");
    }

    [Fact]
    public async Task Create_ReplaysFromEventStream()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericOidc,
            DisplayName: "Test OIDC",
            FlavorData: flavorData));
        Assert.False(result.IsError);

        // Verify the event is in the stream and the projection can replay it.
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IDocumentSession>();

        var events = await session.Events.FetchStreamAsync(result.Value.Id);
        Assert.Single(events);
        Assert.IsType<LoginProviderAddedEvent>(events[0].Data);

        var doc = await session.LoadAsync<LoginProvider>(result.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(doc);
        Assert.Equal("Test OIDC", doc!.DisplayName);
    }

    [Fact]
    public async Task Create_DuplicateDisplayName_Conflicts()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", flavorData));

        var second = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(LoginProviderFlavor.GenericOidc, "Duplicate", flavorData));
        Assert.True(second.IsError);
        Assert.Equal("LoginProvider.DisplayNameTaken", second.FirstError.Code);
    }

    [Fact]
    public async Task Create_UnknownFlavor_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(
            new CreateLoginProviderCommand("NopeFlavor", "X", null));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.UnknownFlavor", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_MissingFlavorRequiredField_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        // EntraId flavor requires TenantId — passing null FlavorData must fail.
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(
            new CreateLoginProviderCommand(LoginProviderFlavor.EntraId, "NoTenant", null));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.FlavorDataInvalid", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_InternalType_DoesNotRequireFlavorOrSecret()
    {
        // Phase 1 addition: Internal-typed providers skip the OIDC-shaped
        // validation entirely (no Flavor lookup, no FlavorData, no
        // ClientId/Secret). They land enabled, since there is no setup step.
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: string.Empty,
            DisplayName: "My Internal " + Guid.NewGuid().ToString("N")[..6],
            FlavorData: null,
            Type: LoginProviderType.Internal));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var config = result.Value;
        Assert.Equal(LoginProviderType.Internal, config.Type);
        Assert.Equal(LoginProviderFlavor.Internal, config.Flavor);
        Assert.Empty(config.ClientId);
        Assert.Null(config.ClientSecretEncrypted);
        Assert.True(config.Enabled);
    }

    [Fact]
    public async Task Create_SamlType_NotYetSupported()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);

        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: "AnyFlavor",
            DisplayName: "SamlAttempt",
            FlavorData: null,
            Type: LoginProviderType.Saml));

        Assert.True(result.IsError);
        Assert.Equal("LoginProvider.TypeNotSupported", result.FirstError.Code);
    }
}
