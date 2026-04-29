using System.Text.Json;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Domain.OAuth.Scopes;

namespace Cocoar.Auth.Tests.Unit.Application;

/// <summary>
/// Pinning tests for the pure helpers in <see cref="OAuthAdminMapping"/>:
/// permission list construction, settings/properties (de)serialization, and
/// state → DTO mapping. These contracts are read by the (eventually-arriving)
/// OpenIddict runtime — drift here causes silent token misbehaviour.
/// </summary>
public class OAuthAdminMappingTests
{
    public class BuildClientPermissions
    {
        [Fact]
        public void Always_includes_all_standard_endpoints()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                Array.Empty<string>(), Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.Contains(OAuthPermissions.Endpoints.Authorization, perms);
            Assert.Contains(OAuthPermissions.Endpoints.Token, perms);
            Assert.Contains(OAuthPermissions.Endpoints.EndSession, perms);
            Assert.Contains(OAuthPermissions.Endpoints.Introspection, perms);
            Assert.Contains(OAuthPermissions.Endpoints.Revocation, perms);
            Assert.Contains(OAuthPermissions.Endpoints.DeviceAuthorization, perms);
        }

        [Fact]
        public void Empty_grant_types_yields_default_authcode_plus_refresh()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                Array.Empty<string>(), Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.Contains(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.Contains(OAuthPermissions.GrantTypes.RefreshToken, perms);
            Assert.Contains(OAuthPermissions.ResponseTypes.Code, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.ClientCredentials, perms);
        }

        [Fact]
        public void Empty_grant_types_for_confidential_also_adds_client_credentials()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                Array.Empty<string>(), Array.Empty<string>(), OAuthClientTypes.Confidential);

            Assert.Contains(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.Contains(OAuthPermissions.GrantTypes.RefreshToken, perms);
            Assert.Contains(OAuthPermissions.GrantTypes.ClientCredentials, perms);
            Assert.Contains(OAuthPermissions.ResponseTypes.Code, perms);
        }

        [Fact]
        public void Explicit_authorization_code_grant_adds_response_type_code()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                new[] { "authorization_code" }, Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.Contains(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.Contains(OAuthPermissions.ResponseTypes.Code, perms);
        }

        [Fact]
        public void Explicit_client_credentials_grant_does_not_add_response_type_code()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                new[] { "client_credentials" }, Array.Empty<string>(), OAuthClientTypes.Confidential);

            Assert.Contains(OAuthPermissions.GrantTypes.ClientCredentials, perms);
            Assert.DoesNotContain(OAuthPermissions.ResponseTypes.Code, perms);
            // Defaults are NOT applied when grants are explicit
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.RefreshToken, perms);
        }

        [Fact]
        public void Unknown_grant_type_is_silently_dropped()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                new[] { "authorization_code", "totally_made_up" },
                Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.Contains(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.DoesNotContain(perms, p => p.Contains("totally_made_up"));
        }

        [Fact]
        public void Device_code_grant_is_mapped_using_full_urn()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                new[] { "urn:ietf:params:oauth:grant-type:device_code" },
                Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.Contains(OAuthPermissions.GrantTypes.DeviceCode, perms);
        }

        [Fact]
        public void Each_scope_is_prefixed_with_scope_prefix_in_order()
        {
            var perms = OAuthAdminMapping.BuildClientPermissions(
                new[] { "authorization_code" },
                new[] { "openid", "profile", "email" },
                OAuthClientTypes.Public);

            Assert.Contains("scp:openid", perms);
            Assert.Contains("scp:profile", perms);
            Assert.Contains("scp:email", perms);
        }
    }

    public class GrantTypeRoundTrip
    {
        [Theory]
        [InlineData("authorization_code")]
        [InlineData("client_credentials")]
        [InlineData("refresh_token")]
        [InlineData("implicit")]
        [InlineData("password")]
        [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
        public void Maps_grant_type_to_permission_and_back(string grantType)
        {
            var permission = OAuthAdminMapping.MapGrantTypeToPermission(grantType);
            Assert.NotNull(permission);

            var roundTripped = OAuthAdminMapping.MapPermissionToGrantType(permission!);
            Assert.Equal(grantType, roundTripped);
        }

        [Fact]
        public void Unknown_grant_type_maps_to_null()
        {
            Assert.Null(OAuthAdminMapping.MapGrantTypeToPermission("xyz"));
        }

        [Fact]
        public void Unknown_permission_maps_to_null()
        {
            Assert.Null(OAuthAdminMapping.MapPermissionToGrantType("gt:unknown"));
        }
    }

    public class ExtractGrantTypes
    {
        [Fact]
        public void Filters_only_grant_type_prefixed_permissions()
        {
            var grants = OAuthAdminMapping.ExtractGrantTypes(new[]
            {
                OAuthPermissions.Endpoints.Token,
                OAuthPermissions.GrantTypes.AuthorizationCode,
                OAuthPermissions.ResponseTypes.Code,
                OAuthPermissions.GrantTypes.RefreshToken,
                "scp:openid",
            });

            Assert.Equal(new[] { "authorization_code", "refresh_token" }, grants);
        }

        [Fact]
        public void Returns_empty_when_no_grant_permissions_present()
        {
            var grants = OAuthAdminMapping.ExtractGrantTypes(new[]
            {
                OAuthPermissions.Endpoints.Token,
                "scp:openid",
            });

            Assert.Empty(grants);
        }
    }

    public class ExtractScopes
    {
        [Fact]
        public void Strips_scope_prefix_from_each_scope_permission()
        {
            var scopes = OAuthAdminMapping.ExtractScopes(new[]
            {
                "scp:openid",
                "scp:profile",
                OAuthPermissions.GrantTypes.AuthorizationCode,
                "scp:email",
            });

            Assert.Equal(new[] { "openid", "profile", "email" }, scopes);
        }

        [Fact]
        public void Returns_empty_when_no_scope_prefixed_permissions()
        {
            var scopes = OAuthAdminMapping.ExtractScopes(new[]
            {
                OAuthPermissions.GrantTypes.AuthorizationCode,
                OAuthPermissions.Endpoints.Token,
            });

            Assert.Empty(scopes);
        }
    }

    public class BuildClientSettings
    {
        [Fact]
        public void Always_writes_access_token_type_and_refresh_token_usage()
        {
            var dto = new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
                AccessTokenType = AccessTokenType.Jwt,
                RefreshTokenUsage = RefreshTokenUsage.ReUse,
            };

            var settings = OAuthAdminMapping.BuildClientSettings(dto);

            Assert.Equal("Jwt", settings[OAuthApplicationSettingKeys.AccessTokenType]);
            Assert.Equal("ReUse", settings[OAuthApplicationSettingKeys.RefreshTokenUsage]);
        }

        [Fact]
        public void Omits_lifetime_keys_when_null()
        {
            var dto = new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
            };

            var settings = OAuthAdminMapping.BuildClientSettings(dto);

            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.IdentityTokenLifetime));
            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.AccessTokenLifetime));
            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.AuthorizationCodeLifetime));
            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime));
            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime));
            Assert.False(settings.ContainsKey(OAuthApplicationSettingKeys.ClientClaimsPrefix));
        }

        [Fact]
        public void Includes_lifetime_keys_when_provided()
        {
            var dto = new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
                IdentityTokenLifetime = 300,
                AccessTokenLifetime = 3600,
                AuthorizationCodeLifetime = 60,
                AbsoluteRefreshTokenLifetime = 2592000,
                SlidingRefreshTokenLifetime = 1296000,
                ClientClaimsPrefix = "client_",
            };

            var settings = OAuthAdminMapping.BuildClientSettings(dto);

            Assert.Equal("300", settings[OAuthApplicationSettingKeys.IdentityTokenLifetime]);
            Assert.Equal("3600", settings[OAuthApplicationSettingKeys.AccessTokenLifetime]);
            Assert.Equal("60", settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime]);
            Assert.Equal("2592000", settings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime]);
            Assert.Equal("1296000", settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime]);
            Assert.Equal("client_", settings[OAuthApplicationSettingKeys.ClientClaimsPrefix]);
        }
    }

    public class BuildClientProperties
    {
        [Fact]
        public void Encodes_all_flags_as_json_elements_under_prefixed_keys()
        {
            var props = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true,
                enableLocal: false, requireConsent: true, allowRemember: false,
                corsOrigins: new[] { "https://a", "https://b" },
                alwaysSend: true, updateClaims: false,
                claims: new[] { new OAuthClientClaimDto { Type = "role", Value = "admin" } },
                roles: new[] { "Admin" });

            var enabled = (JsonElement)props[OAuthApplicationPropertyKeys.Enabled]!;
            Assert.Equal(JsonValueKind.True, enabled.ValueKind);

            var allowBrowser = (JsonElement)props[OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser]!;
            Assert.Equal(JsonValueKind.False, allowBrowser.ValueKind);

            var cors = (JsonElement)props[OAuthApplicationPropertyKeys.AllowedCorsOrigins]!;
            Assert.Equal(JsonValueKind.Array, cors.ValueKind);
            Assert.Equal(2, cors.GetArrayLength());

            var roles = (JsonElement)props[OAuthApplicationPropertyKeys.Roles]!;
            Assert.Equal(JsonValueKind.Array, roles.ValueKind);
            Assert.Equal("Admin", roles[0].GetString());

            var claims = (JsonElement)props[OAuthApplicationPropertyKeys.ClientClaims]!;
            Assert.Equal(JsonValueKind.Array, claims.ValueKind);
            Assert.Equal("role", claims[0].GetProperty("Type").GetString());
            Assert.Equal("admin", claims[0].GetProperty("Value").GetString());
        }

        [Fact]
        public void Round_trips_through_GetBoolProp_and_GetStringListProp()
        {
            var props = OAuthAdminMapping.BuildClientProperties(
                enabled: false, allowBrowser: true, requireSecret: false,
                enableLocal: true, requireConsent: false, allowRemember: true,
                corsOrigins: new[] { "https://x" },
                alwaysSend: false, updateClaims: true,
                claims: Array.Empty<OAuthClientClaimDto>(),
                roles: new[] { "User", "Admin" });

            Assert.False(OAuthAdminMapping.GetBoolProp(props, OAuthApplicationPropertyKeys.Enabled, true));
            Assert.True(OAuthAdminMapping.GetBoolProp(props, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false));
            Assert.Equal(new[] { "https://x" }, OAuthAdminMapping.GetStringListProp(props, OAuthApplicationPropertyKeys.AllowedCorsOrigins));
            Assert.Equal(new[] { "User", "Admin" }, OAuthAdminMapping.GetStringListProp(props, OAuthApplicationPropertyKeys.Roles));
        }
    }

    public class BuildScopeProperties
    {
        [Fact]
        public void Encodes_all_five_scope_flags_under_prefixed_keys()
        {
            var props = OAuthAdminMapping.BuildScopeProperties(
                enabled: true, required: false, emphasize: true,
                showInDiscovery: false, userClaims: new[] { "sub", "email" });

            Assert.Equal(JsonValueKind.True, ((JsonElement)props[ScopePropertyKeys.Enabled]!).ValueKind);
            Assert.Equal(JsonValueKind.False, ((JsonElement)props[ScopePropertyKeys.Required]!).ValueKind);
            Assert.Equal(JsonValueKind.True, ((JsonElement)props[ScopePropertyKeys.Emphasize]!).ValueKind);
            Assert.Equal(JsonValueKind.False, ((JsonElement)props[ScopePropertyKeys.ShowInDiscoveryDocument]!).ValueKind);

            var claims = (JsonElement)props[ScopePropertyKeys.UserClaims]!;
            Assert.Equal(JsonValueKind.Array, claims.ValueKind);
            Assert.Equal(2, claims.GetArrayLength());
        }
    }

    public class GetBoolProp
    {
        [Fact]
        public void Returns_default_when_key_missing()
        {
            var props = new Dictionary<string, object?>();
            Assert.True(OAuthAdminMapping.GetBoolProp(props, "k", true));
            Assert.False(OAuthAdminMapping.GetBoolProp(props, "k", false));
        }

        [Fact]
        public void Returns_default_when_value_null()
        {
            var props = new Dictionary<string, object?> { ["k"] = null };
            Assert.True(OAuthAdminMapping.GetBoolProp(props, "k", true));
        }

        [Fact]
        public void Reads_raw_bool_value()
        {
            var props = new Dictionary<string, object?> { ["k"] = true };
            Assert.True(OAuthAdminMapping.GetBoolProp(props, "k", false));
        }

        [Fact]
        public void Reads_json_true_and_false()
        {
            var t = new Dictionary<string, object?> { ["k"] = JsonSerializer.SerializeToElement(true) };
            var f = new Dictionary<string, object?> { ["k"] = JsonSerializer.SerializeToElement(false) };
            Assert.True(OAuthAdminMapping.GetBoolProp(t, "k", false));
            Assert.False(OAuthAdminMapping.GetBoolProp(f, "k", true));
        }

        [Fact]
        public void Returns_default_when_value_is_unrelated_type()
        {
            var props = new Dictionary<string, object?> { ["k"] = "true" };
            Assert.True(OAuthAdminMapping.GetBoolProp(props, "k", true));
            Assert.False(OAuthAdminMapping.GetBoolProp(props, "k", false));
        }
    }

    public class GetStringListProp
    {
        [Fact]
        public void Returns_empty_when_key_missing()
        {
            Assert.Empty(OAuthAdminMapping.GetStringListProp(new Dictionary<string, object?>(), "k"));
        }

        [Fact]
        public void Reads_json_array_of_strings()
        {
            var props = new Dictionary<string, object?>
            {
                ["k"] = JsonSerializer.SerializeToElement(new[] { "a", "b" }),
            };
            Assert.Equal(new[] { "a", "b" }, OAuthAdminMapping.GetStringListProp(props, "k"));
        }

        [Fact]
        public void Skips_non_string_array_elements()
        {
            var json = JsonSerializer.Deserialize<JsonElement>("[\"a\", 5, \"b\"]");
            var props = new Dictionary<string, object?> { ["k"] = json };
            Assert.Equal(new[] { "a", "b" }, OAuthAdminMapping.GetStringListProp(props, "k"));
        }

        [Fact]
        public void Reads_native_string_enumerable()
        {
            var props = new Dictionary<string, object?> { ["k"] = new List<string> { "x", "y" } };
            Assert.Equal(new[] { "x", "y" }, OAuthAdminMapping.GetStringListProp(props, "k"));
        }

        [Fact]
        public void Returns_empty_for_unrelated_value_kind()
        {
            var props = new Dictionary<string, object?>
            {
                ["k"] = JsonSerializer.SerializeToElement("not-an-array"),
            };
            Assert.Empty(OAuthAdminMapping.GetStringListProp(props, "k"));
        }
    }

    public class GetClaimsProp
    {
        [Fact]
        public void Returns_empty_when_key_missing()
        {
            Assert.Empty(OAuthAdminMapping.GetClaimsProp(new Dictionary<string, object?>()));
        }

        [Fact]
        public void Reads_array_of_type_value_objects()
        {
            var json = JsonSerializer.Deserialize<JsonElement>("""
                [{"Type":"role","Value":"admin"},{"Type":"dept","Value":"eng"}]
                """);
            var props = new Dictionary<string, object?>
            {
                [OAuthApplicationPropertyKeys.ClientClaims] = json,
            };

            var claims = OAuthAdminMapping.GetClaimsProp(props);

            Assert.Equal(2, claims.Count);
            Assert.Equal("role", claims[0].Type);
            Assert.Equal("admin", claims[0].Value);
            Assert.Equal("dept", claims[1].Type);
        }

        [Fact]
        public void Skips_objects_missing_required_string_fields()
        {
            var json = JsonSerializer.Deserialize<JsonElement>("""
                [{"Type":"role"},{"Type":"x","Value":42},{"Type":"ok","Value":"yes"}]
                """);
            var props = new Dictionary<string, object?>
            {
                [OAuthApplicationPropertyKeys.ClientClaims] = json,
            };

            var claims = OAuthAdminMapping.GetClaimsProp(props);

            Assert.Single(claims);
            Assert.Equal("ok", claims[0].Type);
            Assert.Equal("yes", claims[0].Value);
        }

        [Fact]
        public void Returns_empty_when_value_is_not_an_array()
        {
            var props = new Dictionary<string, object?>
            {
                [OAuthApplicationPropertyKeys.ClientClaims] = JsonSerializer.SerializeToElement("scalar"),
            };
            Assert.Empty(OAuthAdminMapping.GetClaimsProp(props));
        }
    }

    public class DictEquals
    {
        [Fact]
        public void Equal_dictionaries_compare_equal()
        {
            var a = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
            var b = new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" };
            Assert.True(OAuthAdminMapping.DictEquals(a, b));
        }

        [Fact]
        public void Different_count_compares_not_equal()
        {
            var a = new Dictionary<string, string> { ["x"] = "1" };
            var b = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
            Assert.False(OAuthAdminMapping.DictEquals(a, b));
        }

        [Fact]
        public void Different_value_for_same_key_compares_not_equal()
        {
            var a = new Dictionary<string, string> { ["x"] = "1" };
            var b = new Dictionary<string, string> { ["x"] = "2" };
            Assert.False(OAuthAdminMapping.DictEquals(a, b));
        }

        [Fact]
        public void Different_keys_compare_not_equal()
        {
            var a = new Dictionary<string, string> { ["x"] = "1" };
            var b = new Dictionary<string, string> { ["y"] = "1" };
            Assert.False(OAuthAdminMapping.DictEquals(a, b));
        }
    }

    public class MapClient
    {
        [Fact]
        public void Defaults_client_and_consent_type_when_state_has_nulls()
        {
            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };
            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Equal(OAuthClientTypes.Public, dto.ClientType);
            Assert.Equal(OAuthConsentTypes.Explicit, dto.ConsentType);
        }

        [Fact]
        public void Parses_access_token_type_and_refresh_token_usage_from_settings()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings =
                {
                    [OAuthApplicationSettingKeys.AccessTokenType] = AccessTokenType.Jwt.ToString(),
                    [OAuthApplicationSettingKeys.RefreshTokenUsage] = RefreshTokenUsage.ReUse.ToString(),
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);

            Assert.Equal(AccessTokenType.Jwt, dto.AccessTokenType);
            Assert.Equal(RefreshTokenUsage.ReUse, dto.RefreshTokenUsage);
        }

        [Fact]
        public void Falls_back_to_defaults_when_setting_unparseable()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings =
                {
                    [OAuthApplicationSettingKeys.AccessTokenType] = "not-an-enum",
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Equal(AccessTokenType.Reference, dto.AccessTokenType);
        }

        [Fact]
        public void Parses_lifetime_settings_as_ints()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings =
                {
                    [OAuthApplicationSettingKeys.AccessTokenLifetime] = "3600",
                    [OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = "not-a-number",
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Equal(3600, dto.AccessTokenLifetime);
            Assert.Null(dto.AbsoluteRefreshTokenLifetime);
            Assert.Null(dto.IdentityTokenLifetime);
        }

        [Fact]
        public void Surfaces_client_claims_prefix_setting()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings = { [OAuthApplicationSettingKeys.ClientClaimsPrefix] = "client_" },
            };

            Assert.Equal("client_", OAuthAdminMapping.MapClient(s).ClientClaimsPrefix);
        }

        [Fact]
        public void Extracts_allowed_grant_types_from_permissions()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Permissions =
                {
                    OAuthPermissions.GrantTypes.AuthorizationCode,
                    OAuthPermissions.GrantTypes.RefreshToken,
                    OAuthPermissions.Endpoints.Token,
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Equal(new[] { "authorization_code", "refresh_token" }, dto.AllowedGrantTypes);
        }

        [Fact]
        public void Round_trips_state_built_from_BuildClientProperties_back_to_dto()
        {
            var props = OAuthAdminMapping.BuildClientProperties(
                enabled: false, allowBrowser: true, requireSecret: false,
                enableLocal: true, requireConsent: true, allowRemember: false,
                corsOrigins: new[] { "https://x.test" },
                alwaysSend: true, updateClaims: false,
                claims: new[] { new OAuthClientClaimDto { Type = "role", Value = "admin" } },
                roles: new[] { "Admin", "User" });

            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Properties = props,
            };

            var dto = OAuthAdminMapping.MapClient(s);

            Assert.False(dto.Enabled);
            Assert.True(dto.AllowAccessTokensViaBrowser);
            Assert.False(dto.RequireClientSecret);
            Assert.True(dto.EnableLocalLogin);
            Assert.True(dto.RequireConsent);
            Assert.False(dto.AllowRememberConsent);
            Assert.True(dto.AlwaysSendClientClaims);
            Assert.False(dto.UpdateAccessTokenClaimsOnRefresh);
            Assert.Equal(new[] { "https://x.test" }, dto.AllowedCorsOrigins);
            Assert.Equal(new[] { "Admin", "User" }, dto.Roles);
            Assert.Single(dto.Claims);
            Assert.Equal("role", dto.Claims[0].Type);
            Assert.Equal("admin", dto.Claims[0].Value);
        }

        [Fact]
        public void Default_property_values_match_dto_defaults_for_missing_props()
        {
            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };
            var dto = OAuthAdminMapping.MapClient(s);

            Assert.True(dto.Enabled);
            Assert.False(dto.AllowAccessTokensViaBrowser);
            Assert.True(dto.RequireClientSecret);
            Assert.True(dto.EnableLocalLogin);
            Assert.False(dto.RequireConsent);
            Assert.True(dto.AllowRememberConsent);
            Assert.False(dto.AlwaysSendClientClaims);
            Assert.False(dto.UpdateAccessTokenClaimsOnRefresh);
            Assert.Empty(dto.AllowedCorsOrigins);
            Assert.Empty(dto.Roles);
            Assert.Empty(dto.Claims);
        }
    }

    public class MapScope
    {
        [Fact]
        public void Copies_all_state_fields_onto_dto()
        {
            var id = Guid.NewGuid();
            var s = new OAuthScopeState
            {
                Id = id,
                Name = "openid",
                DisplayName = "OpenID",
                Description = "OpenID Connect",
                Resources = new() { "resource-a" },
                Enabled = false,
                Required = true,
                Emphasize = true,
                ShowInDiscoveryDocument = false,
                UserClaims = new() { "sub", "email" },
            };

            var dto = OAuthAdminMapping.MapScope(s);

            Assert.Equal(id.ToString(), dto.Id);
            Assert.Equal("openid", dto.Name);
            Assert.Equal("OpenID", dto.DisplayName);
            Assert.Equal("OpenID Connect", dto.Description);
            Assert.Equal(new[] { "resource-a" }, dto.Resources);
            Assert.False(dto.Enabled);
            Assert.True(dto.Required);
            Assert.True(dto.Emphasize);
            Assert.False(dto.ShowInDiscoveryDocument);
            Assert.Equal(new[] { "sub", "email" }, dto.UserClaims);
        }

        [Fact]
        public void Copies_lists_so_mutating_dto_does_not_mutate_state()
        {
            var s = new OAuthScopeState
            {
                Id = Guid.NewGuid(),
                Name = "openid",
                Resources = new() { "a" },
                UserClaims = new() { "sub" },
            };

            var dto = OAuthAdminMapping.MapScope(s);
            dto.Resources.Add("mutated");
            dto.UserClaims.Add("extra");

            Assert.Single(s.Resources);
            Assert.Single(s.UserClaims);
        }
    }

    public class Secrets
    {
        [Fact]
        public void GenerateSecret_produces_unique_values()
        {
            var a = OAuthAdminMapping.GenerateSecret();
            var b = OAuthAdminMapping.GenerateSecret();
            Assert.NotEqual(a, b);
            Assert.False(string.IsNullOrWhiteSpace(a));
        }

        [Fact]
        public void GenerateSecret_returns_base64_encoding_of_32_bytes()
        {
            var s = OAuthAdminMapping.GenerateSecret();
            var bytes = Convert.FromBase64String(s);
            Assert.Equal(32, bytes.Length);
        }

        [Fact]
        public void HashSecret_and_VerifySecret_round_trip()
        {
            // Single hash is intentional — BCrypt at workFactor 12 is slow.
            const string secret = "correct-horse-battery-staple";
            var hash = OAuthAdminMapping.HashSecret(secret);

            Assert.True(OAuthAdminMapping.VerifySecret(secret, hash));
            Assert.False(OAuthAdminMapping.VerifySecret("wrong-secret", hash));
        }

        [Fact]
        public void VerifySecret_returns_false_for_malformed_hash_instead_of_throwing()
        {
            Assert.False(OAuthAdminMapping.VerifySecret("anything", "not-a-bcrypt-hash"));
        }
    }
}
