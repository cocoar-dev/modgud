using System.Security.Claims;
using System.Text.Json;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Wave 3 of the "similar bugs" remediation — finding #15: cross-account takeover.
/// TrustForEmailLink matched an existing account using the user-update SCRIPT's
/// output email, while email_verified only attests the RAW 'email' claim. A script
/// can remap its output email from any claim (e.g. upn), so an attacker with a
/// genuinely-verified throwaway email could set the match key to a victim's address
/// and be auto-linked into the victim's account. The fix requires the match key to
/// BE the verified raw email.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave3Tests : IntegrationTestBase
{
    private const string Issuer = "https://login.microsoftonline.com/test-tenant/v2.0";

    public SecurityAuditWave3Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #15 — a script that remaps the match email to a victim address must NOT
    // auto-link, even with email_verified=true (the verified email is the attacker's).
    [Fact]
    public async Task TrustForEmailLink_RejectsScriptRemappedMatchEmail_NoTakeover()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Vic", lastname: "Tim", acronym: "VT", email: "victim@acme.com", password: "TestPass1234");

        // The provider's update script maps the output email from the attacker-
        // controlled 'upn' claim — NOT the verified 'email' claim.
        var config = await CreateTrustingOidcConfigAsync(
            "(claims) => ({ firstname: claims.given_name, lastname: claims.family_name, email: claims.upn, acronym: 'AT' })");

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        // Attacker: genuinely verified throwaway email, but upn = victim's address.
        var attacker = BuildPrincipal(subject: "attacker-sub-1",
            email: "attacker@evil.com", emailVerified: true, upn: "victim@acme.com");

        var result = await processor.ProcessAsync(attacker, config.Id, ct);

        // RED today: auto-links the attacker's subject into the victim account.
        Assert.False(result.Succeeded);
        Assert.Equal("Idp.EmailNotVerified", result.ErrorCode);
    }

    // Guard against over-blocking: a legit login whose script email IS the verified
    // raw email must still auto-link to the existing account.
    [Fact]
    public async Task TrustForEmailLink_AllowsVerifiedRawEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var legit = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Leg", lastname: "It", acronym: "LE", email: "legit@acme.com", password: "TestPass1234");

        var config = await CreateTrustingOidcConfigAsync(
            "(claims) => ({ firstname: claims.given_name, lastname: claims.family_name, email: claims.email, acronym: 'LE' })");

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var principal = BuildPrincipal(subject: "legit-sub-1",
            email: "legit@acme.com", emailVerified: true, upn: "legit@acme.com");

        var result = await processor.ProcessAsync(principal, config.Id, ct);

        Assert.True(result.Succeeded);
        Assert.Equal(legit.Id, result.UserId);
    }

    private async Task<LoginProvider> CreateTrustingOidcConfigAsync(string userUpdateScript)
    {
        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var flavorData = JsonDocument.Parse("""{"TenantId": "test-tenant"}""");
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.EntraId,
            DisplayName: "Att Entra " + Guid.NewGuid().ToString("N")[..6],
            Slug: $"s{Guid.NewGuid():N}"[..12],
            FlavorData: flavorData));
        Assert.False(result.IsError);
        var id = result.Value.Id;

        session.Events.Append(id, new LoginProviderUpdatedEvent(
            Id: id,
            DisplayName: result.Value.DisplayName,
            Description: null,
            ClientId: "client-id-test",
            Scopes: ["openid", "profile", "email"],
            UserUpdateScript: userUpdateScript,
            StoreRawClaims: true,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: true,
            TrustForEmailLink: true,
            AllowedEmailDomains: null,
            IconName: "microsoft",
            ButtonColorHex: null,
            FlavorData: flavorData,
            UpdatedAt: DateTimeOffset.UtcNow));
        session.Events.Append(id, new LoginProviderEnabledEvent(id, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (await session.LoadAsync<LoginProvider>(id, TestContext.Current.CancellationToken))!;
    }

    private static ClaimsPrincipal BuildPrincipal(string subject, string email, bool emailVerified, string upn)
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("iss", Issuer));
        identity.AddClaim(new Claim("sub", subject));
        identity.AddClaim(new Claim("email", email));
        identity.AddClaim(new Claim("email_verified", emailVerified ? "true" : "false"));
        identity.AddClaim(new Claim("upn", upn));
        identity.AddClaim(new Claim("given_name", "Att"));
        identity.AddClaim(new Claim("family_name", "Acker"));
        return new ClaimsPrincipal(identity);
    }
}
