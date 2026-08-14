using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modgud.Authentication.Setup;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// A non-seeding-on-top-of-boot <see cref="WebApplicationFactory{Program}"/> for
/// cold-start integration tests. It reuses <see cref="ModgudWebApplicationFactory"/>'s
/// test-host overrides (in-memory email, dev cookies, magic-link, host shutdown
/// timeout) but is driven by the <see cref="ColdStartFixture"/> against a blank,
/// per-host database, so the real cold-boot path runs: CREATE DATABASE → schema
/// apply → seed the system realm + OAuth scopes + Internal provider + apps.
///
/// <para>Unlike <see cref="IntegrationTestBase"/> it does NOT pre-create a default
/// admin, does NOT log in, and does NOT call <c>ResetMartenDataAsync</c> — the
/// host boots once and the tests observe the genuine adminless cold state. A test
/// that needs an authenticated control-plane admin asks for one explicitly via
/// <see cref="CreateRealmAdminAndLoginAsync"/>.</para>
///
/// <para>It also installs a <see cref="ColdStartFaultInjection"/> seam so a test
/// can make the bootstrap-invite issuance throw and exercise the realm-create
/// atomicity path.</para>
/// </summary>
public class ColdStartWebApplicationFactory : ModgudWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Reuse the base test-host wiring (env=Testing, in-memory email,
        // dev-friendly cookies, magic-link/email-OTP config, shutdown timeout).
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Fault-injection seam over the real invite service: lets a test
            // force IssueAsync to throw AFTER the realm is provisioned.
            services.AddSingleton<ColdStartFaultInjection>();
            services.AddScoped<PendingAdminInviteService>();
            services.RemoveAll<IPendingAdminInviteService>();
            services.AddScoped<IPendingAdminInviteService>(sp =>
                new FaultInjectingPendingAdminInviteService(
                    sp.GetRequiredService<PendingAdminInviteService>(),
                    sp.GetRequiredService<ColdStartFaultInjection>()));
        });
    }

    /// <summary>
    /// Creates a realm-admin user in the system (control-plane) realm and returns
    /// a cookie-authenticated client. The system realm is the control plane at
    /// cold boot, and a realm:admin passes the <c>control-plane:realm:write</c>
    /// gate via the realm-wide bypass — so the returned client can drive
    /// <c>POST /api/admin/realms</c>.
    /// </summary>
    public async Task<HttpClient> CreateRealmAdminAndLoginAsync(
        string userName = "cpadmin",
        string password = "TestPass1234",
        string email = "cpadmin@coldstart.local")
    {
        await CreateTestUserWithIdentityAsync(
            firstname: "Control",
            lastname: "Plane",
            acronym: userName,
            email: email,
            password: password,
            isRealmAdmin: true);

        var cookieHandler = new CookieContainerHandler();
        var client = CreateDefaultClient(cookieHandler);

        var login = await client.PostAsJsonAsync(
            "/api/account/login",
            new { UserName = userName, Password = password },
            TestContext.Current.CancellationToken);

        if (!login.IsSuccessStatusCode)
        {
            var body = await login.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Cold-start control-plane admin login failed for '{userName}': {login.StatusCode} — {body}");
        }

        return client;
    }
}

/// <summary>
/// Narrow production-shaped test host: Testing keeps every other test override,
/// but Marten's Solo background daemon remains enabled so its maintenance and
/// shutdown lifecycle can be verified independently from the behavioural suite.
/// </summary>
public sealed class SoloDaemonColdStartWebApplicationFactory : ColdStartWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Testing:UseBackgroundProjectionDaemon", "true");
    }
}
