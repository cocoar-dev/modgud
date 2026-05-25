using Modgud.Application.Services;
using OpenIddict.Abstractions;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Drift-pin between the OpenIddict-recognized Settings keys for
/// per-application token lifetimes and the string-literal copies
/// inlined in <see cref="OAuthAdminService"/>.
///
/// <para>The OAuthAdminService writes <c>"tkn_lft:act"</c> /
/// <c>"tkn_lft:reft"</c> into the DCR-created OAuth application's
/// Settings dict so OpenIddict's pipeline reads them natively at
/// token-issue time (manual-smoke bug #30). The Application layer
/// intentionally does NOT reference OpenIddict.Abstractions, so the
/// keys are inlined; this test enforces that the inline copies and
/// the OpenIddict constants stay in lock-step. If OpenIddict ever
/// renames the constants, this test fails loudly instead of
/// silently disabling the per-realm lifetime override.</para>
/// </summary>
public class OpenIddictLifetimeSettingKeysTests
{
    [Fact]
    public void AccessToken_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.AccessToken,
            OAuthAdminService.OpenIddictAccessTokenLifetimeSettingKey);
    }

    [Fact]
    public void RefreshToken_key_matches_OpenIddict_constant()
    {
        Assert.Equal(
            OpenIddictConstants.Settings.TokenLifetimes.RefreshToken,
            OAuthAdminService.OpenIddictRefreshTokenLifetimeSettingKey);
    }
}
