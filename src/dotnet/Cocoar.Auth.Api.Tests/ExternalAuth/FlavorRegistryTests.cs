using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Identity.LoginProviders;

namespace Cocoar.Auth.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class FlavorRegistryTests : IntegrationTestBase
{
    public FlavorRegistryTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public void Registry_ContainsEntraIdAndGenericOidcFlavors()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();

        var keys = registry.All.Select(f => f.Key).ToList();
        Assert.Contains(LoginProviderFlavor.EntraId, keys);
        Assert.Contains(LoginProviderFlavor.GenericOidc, keys);
    }

    [Fact]
    public void EntraIdFlavor_HasEnterpriseDefaultsAndTenantIdSchema()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();
        var flavor = registry.Get(LoginProviderFlavor.EntraId);

        Assert.True(flavor.DefaultStoreRawClaims, "Enterprise flavor should default to storing raw claims");
        Assert.Contains("openid", flavor.DefaultScopes);
        Assert.False(string.IsNullOrWhiteSpace(flavor.DefaultUserUpdateScript));

        var tenantField = flavor.ConfigSchema.Single(f => f.Key == "TenantId");
        Assert.True(tenantField.Required);
    }

    [Fact]
    public void EntraIdFlavor_DeriveEndpoints_BuildsAuthorityFromTenantId()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();
        var flavor = registry.Get(LoginProviderFlavor.EntraId);

        var flavorData = JsonDocument.Parse("""{"TenantId": "00000000-aaaa-bbbb-cccc-000000000000"}""");
        var endpoints = flavor.DeriveEndpoints(flavorData);

        Assert.Equal(
            "https://login.microsoftonline.com/00000000-aaaa-bbbb-cccc-000000000000/v2.0",
            endpoints.Authority);
        Assert.NotNull(endpoints.MetadataUri);
        Assert.EndsWith("/.well-known/openid-configuration", endpoints.MetadataUri);
    }

    [Fact]
    public void EntraIdFlavor_DeriveEndpoints_MissingTenantId_Throws()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();
        var flavor = registry.Get(LoginProviderFlavor.EntraId);

        Assert.Throws<ArgumentException>(() => flavor.DeriveEndpoints(null));
        Assert.Throws<ArgumentException>(() =>
            flavor.DeriveEndpoints(JsonDocument.Parse("""{"Wrong": "x"}""")));
    }

    [Fact]
    public void GenericOidcFlavor_DeriveEndpoints_UsesMetadataUri()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();
        var flavor = registry.Get(LoginProviderFlavor.GenericOidc);

        var flavorData = JsonDocument.Parse("""{"MetadataUri": "https://idp.test/.well-known/openid-configuration"}""");
        var endpoints = flavor.DeriveEndpoints(flavorData);

        Assert.Equal("https://idp.test", endpoints.Authority);
        Assert.Equal("https://idp.test/.well-known/openid-configuration", endpoints.MetadataUri);
    }

    [Fact]
    public void Registry_UnknownKey_ThrowsHelpfulError()
    {
        using var scope = Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LoginProviderFlavorRegistry>();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("DoesNotExist"));
        Assert.Contains("DoesNotExist", ex.Message);
        // Error lists known keys so admin can see what's available
        Assert.Contains(LoginProviderFlavor.EntraId, ex.Message);
    }
}
