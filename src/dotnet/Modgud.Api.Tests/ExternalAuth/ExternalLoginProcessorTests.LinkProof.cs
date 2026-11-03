using System.Net;
using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.SignIn;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// B6-OIDC-01/02 — linking an external identity to the signed-in account is an account
/// change. With only a taken-over session, an attacker could otherwise bind their own
/// IdP identity to the victim's account (a sign-in path that outlives the session) and,
/// with an asserted MFA amr, collect a fresh external factor for account changes.
/// Exercised through the real finish endpoint with real, encrypted app and external
/// cookies; only the factor times in the app session are chosen.
/// </summary>
public partial class ExternalLoginProcessorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]    // the IdP asserts MFA (OIDC-02): still not a proof for this account
    public async Task Link_from_a_session_without_a_recent_proof_is_refused(bool idpAssertsMfa)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateEnabledEntraConfig();
        var subject = $"attacker-{Guid.NewGuid():N}";

        using var response = await FinishAsync(config.Id, subject, proofAge: TimeSpan.FromMinutes(30),
            amr: idpAssertsMfa ? ["mfa"] : null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login?error=Idp.LinkRequiresRecentProof", response.Headers.Location?.OriginalString);
        Assert.Null(await LinkForAsync(subject, ct));
    }

    [Fact]
    public async Task Link_from_a_session_with_a_recent_proof_is_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await CreateEnabledEntraConfig();
        var subject = $"owner-{Guid.NewGuid():N}";

        using var response = await FinishAsync(config.Id, subject, proofAge: TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/profile", response.Headers.Location?.OriginalString);
        var link = await LinkForAsync(subject, ct);
        Assert.NotNull(link);
        Assert.Equal(DefaultUser!.Id, link!.UserId);
    }

    private async Task<ExternalIdentityLink?> LinkForAsync(string subject, CancellationToken ct)
    {
        await using var session = GetTenantedSession();
        return await session.Query<ExternalIdentityLink>()
            .Where(l => l.Issuer == Issuer && l.Subject == subject).FirstOrDefaultAsync(ct);
    }

    /// <summary>The default user's real browser session, whose only proof is a password
    /// sign-in <paramref name="proofAge"/> ago, finishes an external login.</summary>
    private async Task<HttpResponseMessage> FinishAsync(Guid loginProviderId, string subject, TimeSpan proofAge,
        IReadOnlyList<string>? amr = null)
    {
        var origin = new Uri("http://localhost");
        var cookies = new CookieContainerHandler();
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
            var user = await users.FindByIdAsync(DefaultUser!.Id.ToString());
            var principal = await signIn.CreateUserPrincipalAsync(user!);
            var identity = (ClaimsIdentity)principal.Identity!;
            SignInAssurance.Stamp(identity, new Dictionary<string, DateTimeOffset>
            {
                [SignInMethods.Password] = DateTimeOffset.UtcNow - proofAge,
            });
            SignInAssurance.MarkRecorded(identity);
            var created = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .CreateSessionAsync(user!.Id, null, nameof(ExternalLoginProcessorTests), TestContext.Current.CancellationToken);
            Assert.False(created.IsError);
            identity.AddClaim(new Claim(SessionClaimTypes.BrowserSessionId, created.Value.Id.ToString()));

            var app = options.Get(IdentityConstants.ApplicationScheme);
            var external = options.Get(IdentityConstants.ExternalScheme);
            var externalProperties = new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            };
            externalProperties.Items["loginProviderId"] = loginProviderId.ToString("N");
            externalProperties.Items["returnUrl"] = "/profile";
            using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
            cookies.Seed(origin, app.Cookie.Name!, app.TicketDataFormat.Protect(new AuthenticationTicket(principal,
                new AuthenticationProperties
                {
                    IssuedUtc = DateTimeOffset.UtcNow - proofAge,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                }, IdentityConstants.ApplicationScheme)));
            cookies.Seed(origin, external.Cookie.Name!, external.TicketDataFormat.Protect(new AuthenticationTicket(
                BuildExternalPrincipal(subject, email: null, name: "Attacker Identity", amr: amr),
                externalProperties, IdentityConstants.ExternalScheme)));
        }

        var client = Factory.CreateDefaultClient(cookies);
        client.BaseAddress = origin;
        return await client.GetAsync("/api/account/external-login/finish", TestContext.Current.CancellationToken);
    }
}
