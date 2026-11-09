using System.Security.Claims;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// B6-OIDC-05 — an e-mail address the identity provider did not verify must not decide
/// access: not through the domain allowlist, not as the address of a JIT-created account,
/// and not as a profile update. (Auto-linking by e-mail already requires it, audit H3.)
/// </summary>
public partial class ExternalLoginProcessorTests
{
    [Fact]
    public async Task Jit_refuses_an_email_the_provider_did_not_verify()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: true);
        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var result = await processor.ProcessAsync(
            BuildExternalPrincipal("sub-unverified-jit", "boss@acme.com", "Bo Ss", emailVerified: false),
            config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.EmailNotVerified", result.ErrorCode);
        Assert.False(await PersonWithEmailExistsAsync("boss@acme.com"));
    }

    [Fact]
    public async Task The_domain_allowlist_does_not_accept_an_unverified_email()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: true, allowedDomains: ["acme.com"]);
        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var result = await processor.ProcessAsync(
            BuildExternalPrincipal("sub-unverified-domain", "intruder@acme.com", "In Truder", emailVerified: false),
            config.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("Idp.EmailNotVerified", result.ErrorCode);
    }

    [Fact]
    public async Task Jit_does_not_take_an_unverified_preferred_username_as_the_email()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: true);
        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        // No 'email' claim: the test script falls back to preferred_username, which no
        // email_verified vouches for.
        var external = BuildExternalPrincipal("sub-upn-only", email: null, name: "Up N");
        ((ClaimsIdentity)external.Identity!).AddClaim(new Claim("preferred_username", "ceo@acme.com"));

        var result = await processor.ProcessAsync(external, config.Id, default);

        Assert.False(result.Succeeded);
        Assert.False(await PersonWithEmailExistsAsync("ceo@acme.com"));
    }

    [Fact]
    public async Task A_provider_trusted_to_verify_emails_creates_the_account_without_email_verified()
    {
        var config = await CreateEnabledEntraConfig(autoCreate: true, allowedDomains: ["acme.com"], trustEmailsAsVerified: true);
        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();

        var result = await processor.ProcessAsync(
            BuildExternalPrincipal("sub-trusted-idp", "trusted@acme.com", "Tru Sted", emailVerified: false),
            config.Id, default);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.True(await PersonWithEmailExistsAsync("trusted@acme.com"));
    }

    private async Task<bool> PersonWithEmailExistsAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var normalized = email.ToUpperInvariant();
        return await session.Query<Person>().AnyAsync(p => !p.IsDeleted && p.NormalizedEmail == normalized,
            TestContext.Current.CancellationToken);
    }
}
