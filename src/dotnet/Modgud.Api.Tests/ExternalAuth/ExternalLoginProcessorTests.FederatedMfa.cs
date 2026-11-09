using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.SignIn;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// B6-OIDC-04 — a federated sign-in counts as multi-factor for "mfa" or two factor classes
/// (RFC 8176), not for a single factor such as "otp" — unless the admin listed that value
/// as this provider's MFA marker.
/// </summary>
public partial class ExternalLoginProcessorTests
{
    [Theory]
    [InlineData(new[] { "otp" }, null, false)]
    [InlineData(new[] { "hwk" }, null, false)]
    [InlineData(new[] { "pwd", "mfa" }, null, true)]
    [InlineData(new[] { "pwd", "otp" }, null, true)]
    [InlineData(new[] { "otp" }, "otp", true)]
    public async Task A_federated_sign_in_is_multi_factor_only_by_the_rfc_rule_or_the_providers_exception(
        string[] amr, string? providerException, bool expectedMfa)
    {
        var config = await CreateEnabledEntraConfig(
            mfaAmrValues: providerException is null ? null : [providerException]);
        var user = await Factory.CreateTestUserWithIdentityAsync("Am", "R", "AR", $"amr-{Guid.NewGuid():N}@acme.com");
        var subject = $"sub-amr-{Guid.NewGuid():N}";
        await LinkUserAsync(user.Id, config.Id, subject);

        using var scope = Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ExternalLoginProcessor>();
        var result = await processor.ProcessAsync(
            BuildExternalPrincipal(subject, email: null, name: "Am R", amr: amr), config.Id, default);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal(expectedMfa, result.Principal!.HasClaim(SignInAssurance.FederatedMfaClaimType, "true"));
    }
}
