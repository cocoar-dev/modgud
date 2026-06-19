using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using BuildingBlocks.Helper;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 1 of the "similar bugs" remediation (rotation/revocation/refresh cluster).
/// Findings #1, #2, #3: a security-state change must fully revoke live access
/// (OAuth tokens + device-session rows + auth cookies), not just rotate the stamp
/// or delete tracking rows.
///
/// Note on what is asserted:
///  - #1 "revoke all" does NOT rotate the stamp today (only deletes rows), so the
///    OTHER device's cookie survives — asserted directly via a second cookie client.
///  - #2/#3 password reset ALREADY rotates the Identity stamp (so cookies die), but
///    leaves OAuth tokens AND device-session rows alive. We assert the device-session
///    rows are revoked (proves RevokeAllAccessAsync ran), which is RED before the fix.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave1Tests : IntegrationTestBase
{
    private const string Password = "TestPass1234";

    public SecurityAuditWave1Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #1 — self-service "log out everywhere" must invalidate other devices' cookies.
    [Fact]
    public async Task RevokeAllSessions_InvalidatesOtherDeviceCookie_KeepsActingSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var deviceA = await CreateAuthenticatedClientAsync("tu", Password);
        var deviceB = await CreateAuthenticatedClientAsync("tu", Password);

        Assert.Equal(HttpStatusCode.OK, (await deviceB.GetAsync("/api/account/me", ct)).StatusCode);

        var revoke = await deviceA.DeleteAsync("/api/auth/sessions", ct);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Other device must now be rejected at the next SecurityStampValidator pass
        // (ValidationInterval=0 in the harness). RED today: revoke-all only deletes
        // tracking rows, never rotates the stamp, so device B keeps authenticating.
        Assert.Equal(HttpStatusCode.Unauthorized, (await deviceB.GetAsync("/api/account/me", ct)).StatusCode);

        // Acting device survives (RefreshSignInAsync re-issues its cookie).
        Assert.Equal(HttpStatusCode.OK, (await deviceA.GetAsync("/api/account/me", ct)).StatusCode);
    }

    // #2 — admin password reset must revoke the target user's live access.
    [Fact]
    public async Task AdminPasswordReset_RevokesTargetUserSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Target", lastname: "Reset", acronym: "TG", email: "target-reset@test.com", password: Password);
        // Log the target in so a device-session row exists.
        await CreateAuthenticatedClientAsync("tg", Password);
        Assert.True(await SessionCountAsync(target.Id, ct) >= 1);

        // Admin (default Client = realm admin) resets the target's password.
        var resp = await Client.PutAsJsonAsync(
            $"/api/user/{new ShortGuid(target.Id)}/password", new { Password = "NewPass4567!" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // RED today: admin reset rotates the stamp but never calls RevokeAllAccessAsync,
        // so the device-session rows survive (and so do OAuth tokens).
        Assert.Equal(0, await SessionCountAsync(target.Id, ct));
    }

    // #3 — self-service forgot/reset password must revoke the user's live access.
    [Fact]
    public async Task SelfServicePasswordReset_RevokesUserSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        // Default user logs in → session row exists.
        await CreateAuthenticatedClientAsync("tu", Password);
        Assert.True(await SessionCountAsync(DefaultUser!.Id, ct) >= 1);

        string token;
        using (var scope = Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = await um.FindByIdAsync(DefaultUser!.Id.ToString());
            token = await um.GeneratePasswordResetTokenAsync(u!);
        }

        var anon = Factory.CreateDefaultClient(new CookieContainerHandler());
        var resp = await anon.PostAsJsonAsync("/api/account/reset-password",
            new { UserId = DefaultUser!.Id.ToString(), Token = token, NewPassword = "NewPass4567!" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // RED today: reset-password rotates the stamp but never calls RevokeAllAccessAsync.
        Assert.Equal(0, await SessionCountAsync(DefaultUser!.Id, ct));
    }

    private async Task<int> SessionCountAsync(Guid userId, CancellationToken ct)
    {
        await using var session = GetTenantedDocumentSession();
        return await session.Query<UserSession>().CountAsync(s => s.UserId == userId, ct);
    }
}
