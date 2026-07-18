using Modgud.Application.Services;
using OpenIddict.Abstractions;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Drift-pin between the OpenIddict-recognized Settings keys for
/// per-application token lifetimes and the string-literal copies
/// inlined in <see cref="OAuthAdminMapping"/>.
///
/// <para><see cref="OAuthAdminService"/> writes <c>"tkn_lft:act"</c> /
/// <c>"tkn_lft:reft"</c> into the DCR-created OAuth application's
/// Settings dict so OpenIddict's pipeline reads them natively at
/// token-issue time (manual-smoke bug #30), and — since issue #115 —
/// also writes <c>"tkn_lft:act"</c> / <c>"tkn_lft:idt"</c> /
/// <c>"tkn_lft:reft"</c> for standard (admin-created) clients whose
/// Identity/Access/Sliding-Refresh lifetime fields are set. Issue #130
/// added <c>"tkn_lft:auc"</c> for AuthorizationCodeLifetime, which had
/// shipped as a display-only field with no OpenIddict effect until then.
/// The Application layer intentionally does NOT reference
/// OpenIddict.Abstractions, so the keys are inlined on
/// <see cref="OAuthAdminMapping"/>; this test enforces that the inline
/// copies and the OpenIddict constants stay in lock-step. If OpenIddict
/// ever renames the constants, this test fails loudly instead of
/// silently disabling a lifetime override.</para>
/// </summary>
public class OpenIddictLifetimeSettingKeysTests
{
    [Fact]
    public void IdentityToken_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.IdentityToken,
            OAuthAdminMapping.OpenIddictIdentityTokenLifetimeSettingKey);
    }

    [Fact]
    public void AccessToken_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.AccessToken,
            OAuthAdminMapping.OpenIddictAccessTokenLifetimeSettingKey);
    }

    [Fact]
    public void AuthorizationCode_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.AuthorizationCode,
            OAuthAdminMapping.OpenIddictAuthorizationCodeLifetimeSettingKey);
    }

    [Fact]
    public void RefreshToken_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.RefreshToken,
            OAuthAdminMapping.OpenIddictRefreshTokenLifetimeSettingKey);
    }
}
