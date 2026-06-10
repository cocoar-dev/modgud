using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;
using Modgud.Domain.RealmSettings;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Security-audit wave 2 (medium findings): magic-link must not bypass TOTP (M1),
/// email-OTP is only issuable to users who enabled it (M2), login is gated on a
/// verified email when the realm requires it (M4), and a domain maps to at most
/// one active realm (M10).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Security")]
public class AuthHardeningWave2Tests : IntegrationTestBase
{
    public AuthHardeningWave2Tests(SharedPostgresFixture fixture) : base(fixture) { }

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ── M1 — magic-link requires TOTP step-up ────────────────────────────────

    [Fact]
    public async Task MagicLink_WithTotpEnabled_RequiresStepUp_NotFullLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Totp", "User", "tpu", "tpu@test.com", "TestPass1234", isRealmAdmin: false);

        // Enable TOTP on the account.
        using (var scope = Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var appUser = await um.FindByIdAsync(user.Id.ToString());
            await um.ResetAuthenticatorKeyAsync(appUser!);
            await um.SetTwoFactorEnabledAsync(appUser!, true);
        }

        // Plant a consumable magic-link challenge (mailbox already proven).
        var token = "wave2-magic-" + Guid.NewGuid().ToString("N");
        using (var s = GetTenantedDocumentSession("system"))
        {
            s.Store(new MagicLinkChallenge
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = Sha256Hex(token),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await s.SaveChangesAsync(ct);
        }

        var client = Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/account/magic-link/login",
            new { UserId = user.Id, Token = token }, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        // The magic-link is NOT a full login for a TOTP user — it returns the
        // step-up challenge, not "Login successful".
        Assert.Contains("RequiresMfa", body);
        Assert.DoesNotContain("Login successful", body);
    }

    // ── M2 — email-OTP only for users who enabled it ─────────────────────────

    [Fact]
    public async Task EmailOtp_Request_ForUserWithoutEmailOtpEnabled_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Otp", "Off", "ooff", "ooff@test.com", "TestPass1234", isRealmAdmin: false);

        using var _ = TenantContext.Enter("system");
        using var scope = Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();

        var result = await svc.RequestOtpAsync(user.Id, ct);

        Assert.True(result.IsError);
        Assert.Equal("EmailOtp.NotEnabled", result.FirstError.Code);
    }

    // ── M4 — login gated on a verified email when the realm requires it ──────

    [Fact]
    public async Task Login_UnverifiedEmail_WhenRealmRequiresVerification_Is403()
    {
        var ct = TestContext.Current.CancellationToken;

        // Realm policy: verified emails are required.
        using (var s = GetTenantedDocumentSession("system"))
        {
            s.Store(new RealmSettings
            {
                SelfRegistration = new SelfRegistrationSettings
                {
                    Enabled = true,
                    RequireEmailVerification = true,
                },
            });
            await s.SaveChangesAsync(ct);
        }

        // Active user, correct password, but unverified email.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Unv", "Erified", "uver", "uver@test.com", "TestPass1234", isRealmAdmin: false);
        using (var scope = Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var appUser = await um.FindByIdAsync(user.Id.ToString());
            appUser!.EmailConfirmed = false;
            await um.UpdateAsync(appUser);
        }

        var client = Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "uver", Password = "TestPass1234" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("Account.EmailNotVerified", await resp.Content.ReadAsStringAsync(ct));
    }

    // ── M10 — a domain maps to at most one active realm ──────────────────────

    [Fact]
    public async Task CreateRealm_WithDomainHeldByAnotherActiveRealm_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = Factory.Services.GetRequiredService<IRealmProvisioningService>();

        // The always-present, active system realm already owns its domains.
        var system = await svc.GetRealmBySlugAsync(TenantConstants.SystemTenantId, ct);
        Assert.NotNull(system);
        Assert.True(system!.IsActive);
        var takenDomain = system.Domains[0];

        // Fails fast on the uniqueness check — before any database is created.
        var result = await svc.CreateRealmAsync(new CreateRealmDto
        {
            Slug = "m10dup",
            DisplayName = "Duplicate Domain",
            Domains = [takenDomain],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@m10.test" },
        }, ct);

        Assert.True(result.IsError);
        Assert.Equal("Realm.DomainTaken", result.FirstError.Code);
    }
}
