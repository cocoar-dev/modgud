using Modgud.Domain.OAuth.Applications;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Pins the wire-format string values of the custom OAuth application setting / property keys.
/// These keys are persisted via OpenIddict's Settings/Properties dictionaries — drift here means
/// existing rows in the database silently stop being read (or worse, get duplicated under a new key).
/// One assertion per constant on purpose: changing any value should make exactly one test fail and
/// be impossible to merge unnoticed.
/// </summary>
public class OAuthApplicationKeysTests
{
    public class SettingKeys
    {
        [Fact]
        public void AccessTokenType_value_is_pinned() =>
            Assert.Equal("modgud:access_token_type", OAuthApplicationSettingKeys.AccessTokenType);

        [Fact]
        public void RefreshTokenUsage_value_is_pinned() =>
            Assert.Equal("modgud:refresh_token_usage", OAuthApplicationSettingKeys.RefreshTokenUsage);

        [Fact]
        public void IdentityTokenLifetime_value_is_pinned() =>
            Assert.Equal("modgud:identity_token_lifetime", OAuthApplicationSettingKeys.IdentityTokenLifetime);

        [Fact]
        public void AccessTokenLifetime_value_is_pinned() =>
            Assert.Equal("modgud:access_token_lifetime", OAuthApplicationSettingKeys.AccessTokenLifetime);

        [Fact]
        public void AuthorizationCodeLifetime_value_is_pinned() =>
            Assert.Equal("modgud:authorization_code_lifetime", OAuthApplicationSettingKeys.AuthorizationCodeLifetime);

        [Fact]
        public void AbsoluteRefreshTokenLifetime_value_is_pinned() =>
            Assert.Equal("modgud:absolute_refresh_token_lifetime", OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime);

        [Fact]
        public void SlidingRefreshTokenLifetime_value_is_pinned() =>
            Assert.Equal("modgud:sliding_refresh_token_lifetime", OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime);

        [Fact]
        public void ClientClaimsPrefix_value_is_pinned() =>
            Assert.Equal("modgud:client_claims_prefix", OAuthApplicationSettingKeys.ClientClaimsPrefix);

        [Fact]
        public void All_setting_keys_use_the_modgud_prefix()
        {
            // Modgud's custom keys live under the "modgud:" namespace so they cannot collide
            // with OpenIddict's own setting keys. If anyone introduces a key without the prefix
            // it'll shadow or get shadowed by an OpenIddict key — catch that immediately.
            var keys = new[]
            {
                OAuthApplicationSettingKeys.AccessTokenType,
                OAuthApplicationSettingKeys.RefreshTokenUsage,
                OAuthApplicationSettingKeys.IdentityTokenLifetime,
                OAuthApplicationSettingKeys.AccessTokenLifetime,
                OAuthApplicationSettingKeys.AuthorizationCodeLifetime,
                OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime,
                OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime,
                OAuthApplicationSettingKeys.ClientClaimsPrefix,
            };

            foreach (var k in keys)
                Assert.StartsWith("modgud:", k);
        }

        [Fact]
        public void All_setting_keys_are_unique()
        {
            var keys = new[]
            {
                OAuthApplicationSettingKeys.AccessTokenType,
                OAuthApplicationSettingKeys.RefreshTokenUsage,
                OAuthApplicationSettingKeys.IdentityTokenLifetime,
                OAuthApplicationSettingKeys.AccessTokenLifetime,
                OAuthApplicationSettingKeys.AuthorizationCodeLifetime,
                OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime,
                OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime,
                OAuthApplicationSettingKeys.ClientClaimsPrefix,
            };

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }
    }

    public class PropertyKeys
    {
        [Fact]
        public void Enabled_value_is_pinned() =>
            Assert.Equal("modgud:enabled", OAuthApplicationPropertyKeys.Enabled);

        [Fact]
        public void AllowAccessTokensViaBrowser_value_is_pinned() =>
            Assert.Equal("modgud:allow_access_tokens_via_browser", OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser);

        [Fact]
        public void RequireClientSecret_value_is_pinned() =>
            Assert.Equal("modgud:require_client_secret", OAuthApplicationPropertyKeys.RequireClientSecret);

        [Fact]
        public void EnableLocalLogin_value_is_pinned() =>
            Assert.Equal("modgud:enable_local_login", OAuthApplicationPropertyKeys.EnableLocalLogin);

        [Fact]
        public void RequireConsent_value_is_pinned() =>
            Assert.Equal("modgud:require_consent", OAuthApplicationPropertyKeys.RequireConsent);

        [Fact]
        public void AllowRememberConsent_value_is_pinned() =>
            Assert.Equal("modgud:allow_remember_consent", OAuthApplicationPropertyKeys.AllowRememberConsent);

        [Fact]
        public void AllowedCorsOrigins_value_is_pinned() =>
            Assert.Equal("modgud:allowed_cors_origins", OAuthApplicationPropertyKeys.AllowedCorsOrigins);

        [Fact]
        public void AlwaysSendClientClaims_value_is_pinned() =>
            Assert.Equal("modgud:always_send_client_claims", OAuthApplicationPropertyKeys.AlwaysSendClientClaims);

        [Fact]
        public void UpdateAccessTokenClaimsOnRefresh_value_is_pinned() =>
            Assert.Equal("modgud:update_access_token_claims_on_refresh", OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh);

        [Fact]
        public void ClientClaims_value_is_pinned() =>
            Assert.Equal("modgud:client_claims", OAuthApplicationPropertyKeys.ClientClaims);

        [Fact]
        public void Roles_value_is_pinned() =>
            Assert.Equal("modgud:roles", OAuthApplicationPropertyKeys.Roles);

        [Fact]
        public void All_property_keys_use_the_modgud_prefix()
        {
            var keys = new[]
            {
                OAuthApplicationPropertyKeys.Enabled,
                OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser,
                OAuthApplicationPropertyKeys.RequireClientSecret,
                OAuthApplicationPropertyKeys.EnableLocalLogin,
                OAuthApplicationPropertyKeys.RequireConsent,
                OAuthApplicationPropertyKeys.AllowRememberConsent,
                OAuthApplicationPropertyKeys.AllowedCorsOrigins,
                OAuthApplicationPropertyKeys.AlwaysSendClientClaims,
                OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh,
                OAuthApplicationPropertyKeys.ClientClaims,
                OAuthApplicationPropertyKeys.Roles,
            };

            foreach (var k in keys)
                Assert.StartsWith("modgud:", k);
        }

        [Fact]
        public void All_property_keys_are_unique()
        {
            var keys = new[]
            {
                OAuthApplicationPropertyKeys.Enabled,
                OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser,
                OAuthApplicationPropertyKeys.RequireClientSecret,
                OAuthApplicationPropertyKeys.EnableLocalLogin,
                OAuthApplicationPropertyKeys.RequireConsent,
                OAuthApplicationPropertyKeys.AllowRememberConsent,
                OAuthApplicationPropertyKeys.AllowedCorsOrigins,
                OAuthApplicationPropertyKeys.AlwaysSendClientClaims,
                OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh,
                OAuthApplicationPropertyKeys.ClientClaims,
                OAuthApplicationPropertyKeys.Roles,
            };

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }
    }

    public class CrossNamespace
    {
        [Fact]
        public void Setting_and_property_keys_do_not_overlap()
        {
            // Settings and Properties are stored in two separate dictionaries on the
            // OpenIddict application document. A key shared between both is a strong
            // smell that one was copied without rename.
            var settings = new[]
            {
                OAuthApplicationSettingKeys.AccessTokenType,
                OAuthApplicationSettingKeys.RefreshTokenUsage,
                OAuthApplicationSettingKeys.IdentityTokenLifetime,
                OAuthApplicationSettingKeys.AccessTokenLifetime,
                OAuthApplicationSettingKeys.AuthorizationCodeLifetime,
                OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime,
                OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime,
                OAuthApplicationSettingKeys.ClientClaimsPrefix,
            };

            var properties = new[]
            {
                OAuthApplicationPropertyKeys.Enabled,
                OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser,
                OAuthApplicationPropertyKeys.RequireClientSecret,
                OAuthApplicationPropertyKeys.EnableLocalLogin,
                OAuthApplicationPropertyKeys.RequireConsent,
                OAuthApplicationPropertyKeys.AllowRememberConsent,
                OAuthApplicationPropertyKeys.AllowedCorsOrigins,
                OAuthApplicationPropertyKeys.AlwaysSendClientClaims,
                OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh,
                OAuthApplicationPropertyKeys.ClientClaims,
                OAuthApplicationPropertyKeys.Roles,
            };

            Assert.Empty(settings.Intersect(properties));
        }
    }
}
