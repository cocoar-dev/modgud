using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Applications;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 0 — pins the effective-settings resolver end-to-end against a
/// real tenant DB: the ApplicationSettings doc is Marten-registered, a no-App
/// resolution returns the realm settings unchanged (zero-behaviour), an
/// in-context App with no overrides inherits the realm + the Application-default
/// posture, and a persisted override merges field-by-field over the realm.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ApplicationSettingsResolverTests : IntegrationTestBase
{
    public ApplicationSettingsResolverTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoApplication_Returns_RealmSettings_Unchanged()
    {
        using var scope = NewSystemTenantScope();
        var ct = TestContext.Current.CancellationToken;

        await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
            .PatchAsync(new UpdateRealmSettingsDto
            {
                NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
                Branding = new UpdateBrandingSettingsDto { ProductName = "RealmProduct", PrimaryColor = "#112233" },
            }, ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationSettingsResolver>();
        var eff = await resolver.ResolveAsync(applicationId: null, ct);

        Assert.True(eff.NativeGrants!.Enabled);
        Assert.Equal("RealmProduct", eff.Branding!.ProductName);
        Assert.Equal("#112233", eff.Branding.PrimaryColor);
        Assert.Null(eff.SelfRegPosture); // no Application in context
        Assert.Null(eff.Origin);
    }

    [Fact]
    public async Task Application_WithoutOverrides_Inherits_Realm_And_Defaults_Posture()
    {
        using var scope = NewSystemTenantScope();
        var ct = TestContext.Current.CancellationToken;

        await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
            .PatchAsync(new UpdateRealmSettingsDto
            {
                Branding = new UpdateBrandingSettingsDto { ProductName = "RealmProduct" },
            }, ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationSettingsResolver>();
        var eff = await resolver.ResolveAsync(applicationId: Guid.NewGuid(), ct);

        Assert.Equal("RealmProduct", eff.Branding!.ProductName);     // inherited
        Assert.Equal(SelfRegPosture.JitOnOtp, eff.SelfRegPosture);   // Application default
    }

    [Fact]
    public async Task Application_Override_Merges_FieldByField_Over_Realm()
    {
        using var scope = NewSystemTenantScope();
        var ct = TestContext.Current.CancellationToken;

        await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
            .PatchAsync(new UpdateRealmSettingsDto
            {
                Branding = new UpdateBrandingSettingsDto { ProductName = "RealmProduct", PrimaryColor = "#112233" },
            }, ct);

        var appId = Guid.NewGuid();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApplicationSettings
        {
            Id = appId,
            CreatedAt = DateTimeOffset.UtcNow,
            // Override only the product name + posture; primary color must inherit the realm.
            Branding = new BrandingSettings { ProductName = "AcmeList" },
            SelfRegistration = new ApplicationSelfRegistration { Posture = SelfRegPosture.ExplicitEndpoint },
            Origin = new ApplicationOrigin { Subdomain = "acmelist.cocoar.app" },
        });
        await session.SaveChangesAsync(ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationSettingsResolver>();
        var eff = await resolver.ResolveAsync(appId, ct);

        Assert.Equal("AcmeList", eff.Branding!.ProductName);          // overridden
        Assert.Equal("#112233", eff.Branding.PrimaryColor);          // inherited from realm
        Assert.Equal(SelfRegPosture.ExplicitEndpoint, eff.SelfRegPosture);
        Assert.Equal("acmelist.cocoar.app", eff.Origin!.Subdomain);
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
