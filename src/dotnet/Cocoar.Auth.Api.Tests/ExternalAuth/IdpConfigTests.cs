using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Authentication.Api.Admin.IdentityProviders.Commands;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Wolverine;

namespace Cocoar.Auth.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class IdpConfigTests : IntegrationTestBase
{
    public IdpConfigTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Create_PersistsIdpConfig_WithFlavorDefaults()
    {
        // Exit criterion for Phase 1: IdpConfig can be created via Wolverine-style
        // command handler, the event is persisted, the inline projection
        // materializes the document with flavor-derived defaults.
        using var scope = Factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var flavorData = JsonDocument.Parse("""{"TenantId": "11111111-2222-3333-4444-555555555555"}""");
        var command = new CreateIdpConfigCommand(
            Flavor: IdpFlavor.EntraId,
            DisplayName: "Acme Entra",
            FlavorData: flavorData);

        var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(command);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");
        var config = result.Value;

        Assert.NotEqual(Guid.Empty, config.Id);
        Assert.Equal(IdpFlavor.EntraId, config.Flavor);
        Assert.Equal("Acme Entra", config.DisplayName);
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
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(new CreateIdpConfigCommand(
            Flavor: IdpFlavor.GenericOidc,
            DisplayName: "Test OIDC",
            FlavorData: flavorData));
        Assert.False(result.IsError);

        // Verify the event is in the stream and the projection can replay it.
        using var scope2 = Factory.Services.CreateScope();
        var session = scope2.ServiceProvider.GetRequiredService<IDocumentSession>();

        var events = await session.Events.FetchStreamAsync(result.Value.Id);
        Assert.Single(events);
        Assert.IsType<IdpConfigAddedEvent>(events[0].Data);

        var doc = await session.LoadAsync<IdpConfig>(result.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(doc);
        Assert.Equal("Test OIDC", doc!.DisplayName);
    }

    [Fact]
    public async Task Create_DuplicateDisplayName_Conflicts()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        await bus.InvokeAsync<ErrorOr<IdpConfig>>(new CreateIdpConfigCommand(IdpFlavor.GenericOidc, "Duplicate", flavorData));

        var second = await bus.InvokeAsync<ErrorOr<IdpConfig>>(new CreateIdpConfigCommand(IdpFlavor.GenericOidc, "Duplicate", flavorData));
        Assert.True(second.IsError);
        Assert.Equal("IdpConfig.DisplayNameTaken", second.FirstError.Code);
    }

    [Fact]
    public async Task Create_UnknownFlavor_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(
            new CreateIdpConfigCommand("NopeFlavor", "X", null));

        Assert.True(result.IsError);
        Assert.Equal("IdpConfig.UnknownFlavor", result.FirstError.Code);
    }

    [Fact]
    public async Task Create_MissingFlavorRequiredField_ValidationError()
    {
        using var scope = Factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // EntraId flavor requires TenantId — passing null FlavorData must fail.
        var result = await bus.InvokeAsync<ErrorOr<IdpConfig>>(
            new CreateIdpConfigCommand(IdpFlavor.EntraId, "NoTenant", null));

        Assert.True(result.IsError);
        Assert.Equal("IdpConfig.FlavorDataInvalid", result.FirstError.Code);
    }
}
