using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 6 — per-Application email branding. An OTP email triggered on an
/// App subdomain carries the App's branding product name (merged over the realm),
/// not the hardcoded "Modgud". Verified via the rendered email body captured by the
/// in-memory email service.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeEmailBrandingTests : IntegrationTestBase
{
    private const string Email = "test@test.com"; // the seeded (confirmed) DefaultUser

    public NativeEmailBrandingTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Otp_Email_On_App_Subdomain_Uses_App_Branding_Product_Name()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnableRealmNativeGrantsAsync();
        var app = await CreateAppAsync("p6-brand-app");
        await StoreApplicationSettingsAsync(new ApplicationSettings
        {
            Id = app.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Branding = new BrandingSettings { ProductName = "amZettel" },
        });
        await MapApplicationDomainsAsync(("p6-brand.localhost", app.Id));

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/account/native/otp/request")
        {
            Content = JsonContent.Create(new { Email }),
        };
        req.Headers.Host = "p6-brand.localhost";
        (await Client.SendAsync(req, ct)).EnsureSuccessStatusCode();

        var msg = emailService.GetLastEmailTo(Email);
        Assert.NotNull(msg);
        // The App's branding product name flowed into the email ({{AppName}}, which
        // the OTP template renders into the subject), instead of hardcoded "Modgud".
        Assert.Contains("amZettel", msg!.Subject);
        Assert.DoesNotContain("Modgud", msg.Subject);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task EnableRealmNativeGrantsAsync()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            NativeGrants = new UpdateNativeGrantSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
    }

    private async Task<App> CreateAppAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id, Slug: slug, DisplayName: slug, Description: null, Permissions: [], IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (await session.LoadAsync<App>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task StoreApplicationSettingsAsync(ApplicationSettings settings)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(settings);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task MapApplicationDomainsAsync(params (string Host, Guid AppId)[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var globalStore = Factory.Services.GetRequiredService<IGlobalStore>();
        await using (var session = globalStore.LightweightSession())
        {
            var systemRealm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == "system", ct);
            Assert.NotNull(systemRealm);
            foreach (var (host, appId) in entries)
                systemRealm!.ApplicationDomains[host] = appId;
            session.Store(systemRealm!);
            await session.SaveChangesAsync(ct);
        }

        Factory.Services.GetRequiredService<IRealmCache>().Invalidate();
    }
}
