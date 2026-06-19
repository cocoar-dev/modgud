using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 8 of the "similar bugs" remediation — Low/hygiene cluster. Covers the
/// security-meaningful items with deterministic tests: 2FA factor changes revoke
/// OAuth tokens (#10/#12) and the store getters read the authoritative
/// UserSecurityData rather than the transient mirror (#18). The remaining cluster
/// items are comment/PII/cache/invite hardening verified by the full suite.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave8Tests : IntegrationTestBase
{
    public SecurityAuditWave8Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #10 — disabling TOTP must revoke the user's live OAuth reference tokens, not
    // just rotate the stamp (stock introspection trusts store status).
    [Fact]
    public async Task DisableTotp_RevokesOAuthTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var tokenId = await SeedTokenAsync(DefaultUser!.Id.ToString(), ct);

        var resp = await Client.PostAsync("/api/account/mfa/disable", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await AssertTokenRevokedAsync(tokenId, ct);
    }

    // #10/#12 — same for Email-OTP disable.
    [Fact]
    public async Task DisableEmailOtp_RevokesOAuthTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var tokenId = await SeedTokenAsync(DefaultUser!.Id.ToString(), ct);

        var resp = await Client.PostAsync("/api/account/email-otp/disable", null, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await AssertTokenRevokedAsync(tokenId, ct);
    }

    // #18 — GetTwoFactorEnabledAsync must read the AUTHORITATIVE UserSecurityData,
    // not the transient ApplicationUser mirror, so a raw-loaded user handed to the
    // 2FA step-up gate can't skip the second factor. RED before the fix: the mirror
    // (false) is returned even though UserSecurityData says true.
    [Fact]
    public async Task GetTwoFactorEnabled_ReadsAuthoritativeSecurityData()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Two", lastname: "Factor", acronym: "TF", email: "tf@test.com", password: "TestPass1234");

        // Flip ONLY the authoritative store, leaving the ApplicationUser mirror false.
        await using (var session = GetTenantedDocumentSession())
        {
            var sd = await session.LoadAsync<UserSecurityData>(user.Id, ct);
            sd!.TwoFactorEnabled = true;
            session.Store(sd);
            await session.SaveChangesAsync(ct);
        }

        using var scope = Factory.Services.CreateScope();
        var store = (IUserTwoFactorStore<ApplicationUser>)scope.ServiceProvider
            .GetRequiredService<IUserStore<ApplicationUser>>();

        // Raw-load the user (mirror) — its TwoFactorEnabled is still false.
        ApplicationUser rawUser;
        await using (var session = GetTenantedDocumentSession())
        {
            rawUser = (await session.LoadAsync<ApplicationUser>(user.Id, ct))!;
        }
        Assert.False(rawUser.TwoFactorEnabled);

        var authoritative = await store.GetTwoFactorEnabledAsync(rawUser, ct);
        Assert.True(authoritative);
    }

    private async Task<string> SeedTokenAsync(string subject, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        await using var session = GetTenantedDocumentSession();
        session.Store(new OpenIddictTokenDocument
        {
            Id = id,
            Subject = subject,
            ApplicationId = "client-w8",
            Status = OpenIddictConstants.Statuses.Valid,
            Type = OpenIddictConstants.TokenTypeHints.AccessToken,
            CreationDate = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
        return id;
    }

    private async Task AssertTokenRevokedAsync(string tokenId, CancellationToken ct)
    {
        await using var session = GetTenantedDocumentSession();
        var token = await session.LoadAsync<OpenIddictTokenDocument>(tokenId, ct);
        Assert.NotNull(token);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, token!.Status);
    }
}
