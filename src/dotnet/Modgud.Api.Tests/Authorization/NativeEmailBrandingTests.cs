using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Identity;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR-0011 Phase 6 — per-Application email branding. When an OTP email is issued
/// with an Application in context (Host-resolved), it carries the App's branding
/// product name (merged over the realm), not the hardcoded "Modgud". Driven through
/// <see cref="IEmailOtpService"/> with a faked request context rather than the live
/// rate-limited HTTP endpoint (its per-IP budget is a scarce, collection-shared
/// resource keyed on the null test RemoteIp); the resolver reads the ambient
/// HttpContext exactly as it does in the real pipeline.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class NativeEmailBrandingTests : IntegrationTestBase
{
    private const string Email = "test@test.com"; // the seeded (confirmed) DefaultUser

    public NativeEmailBrandingTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Otp_Email_With_App_Context_Uses_App_Branding_Product_Name()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        // Fake the per-request context the real pipeline would set: tenant + the
        // pinned Application (as if the request arrived on the App subdomain).
        var http = new DefaultHttpContext();
        http.Items[TenantConstants.HttpContextTenantIdKey] = "system";
        http.Items[TenantConstants.HttpContextApplicationIdKey] = appId;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        // The App overrides its branding product name.
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApplicationSettings
        {
            Id = appId,
            CreatedAt = DateTimeOffset.UtcNow,
            Branding = new BrandingSettings { ProductName = "amZettel" },
        });
        await session.SaveChangesAsync(ct);

        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();

        var otp = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();
        var result = await otp.RequestNativeOtpAsync(DefaultUser!.Id, ct);
        Assert.False(result.IsError, "RequestNativeOtpAsync should issue a code for the confirmed DefaultUser");

        var msg = emailService.GetLastEmailTo(Email);
        Assert.NotNull(msg);
        // The App's branding product name flowed into the email ({{AppName}}, which
        // the OTP template renders into the subject), instead of hardcoded "Modgud".
        Assert.Contains("amZettel", msg!.Subject);
        Assert.DoesNotContain("Modgud", msg.Subject);
    }
}
