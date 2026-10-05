using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// ADR 0025 — the realm's sign-in policy through <see cref="IRealmSettingsService"/>:
/// "never configured" is visible as null on read, the first save writes a complete policy
/// over the defaults, and impossible or malformed policies are rejected.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmSignInPolicySettingsTests : IntegrationTestBase
{
    public RealmSignInPolicySettingsTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Impossible_Or_Malformed_Policies_Are_Rejected_Then_Unconfigured_Patches_Over_Defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();

        // Never configured: null on the wire, not defaults dressed up as saved values.
        Assert.Null((await settings.GetDtoAsync(ct)).SignIn);

        // Multi with only an e-mail code and no second factor, no passkey, no password.
        var impossible = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto
            {
                MinimumLevel = "Multi", Password = false, EmailCode = true, Passkey = false, Totp = false,
            },
        }, ct);
        Assert.True(impossible.IsError);
        Assert.Contains("Multi", impossible.FirstError.Description);

        // The administration level counts too.
        Assert.True((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto
            {
                MinimumLevel = "Single", AdministrationMinimumLevel = "Multi",
                Password = false, EmailCode = true, Passkey = false, Totp = false,
            },
        }, ct)).IsError);

        // No way to sign in at all.
        Assert.True((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto
            {
                MinimumLevel = "Single", AdministrationMinimumLevel = "Single",
                Password = false, EmailCode = false, Passkey = false,
            },
        }, ct)).IsError);

        Assert.True((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { MinimumLevel = "Strong" },
        }, ct)).IsError);
        Assert.True((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { SetupGraceDays = 366 },
        }, ct)).IsError);
        Assert.True((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { OwnFactorNotOffered = "Maybe" },
        }, ct)).IsError);

        // Rejected patches left the realm unconfigured.
        Assert.Null((await settings.GetDtoAsync(ct)).SignIn);

        var patched = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { MinimumLevel = "Multi", SetupGraceDays = 5, EmailCode = true },
        }, ct);
        Assert.False(patched.IsError, patched.IsError ? patched.FirstError.Description : string.Empty);

        var read = (await settings.GetDtoAsync(ct)).SignIn!;
        Assert.Equal("Multi", read.MinimumLevel);
        Assert.Equal(5, read.SetupGraceDays);
        Assert.True(read.EmailCode);
        // Everything the patch did not name comes from SignInPolicy.Defaults.
        Assert.Equal("Multi", read.AdministrationMinimumLevel);
        Assert.True(read.Password);
        Assert.True(read.Passkey);
        Assert.True(read.Totp);
        Assert.True(read.EmailAfterPassword);
        Assert.Equal("RequireViaBrowser", read.OwnFactorNotOffered);

        // A later patch merges field by field.
        Assert.False((await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            SignIn = new UpdateSignInPolicyDto { OwnFactorNotOffered = "Ignore" },
        }, ct)).IsError);
        var again = (await settings.GetDtoAsync(ct)).SignIn!;
        Assert.Equal("Ignore", again.OwnFactorNotOffered);
        Assert.Equal("Multi", again.MinimumLevel);
        Assert.Equal(5, again.SetupGraceDays);
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
