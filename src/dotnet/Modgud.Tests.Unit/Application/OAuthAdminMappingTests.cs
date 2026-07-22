using System.Text.Json;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Scopes;

namespace Modgud.Tests.Unit.Application;

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
        public void Empty_grant_types_for_public_yields_no_grant_permissions()
        {
            // Pin the post-fallback-removal behaviour: an admin who creates
            // a Public client without picking grant types ends up with a
            // client that holds NO token-flow permissions and cannot mint
            // tokens. Better than silently granting authcode+refresh to
            // every freshly-created client.
            var perms = OAuthAdminMapping.BuildClientPermissions(
                Array.Empty<string>(), Array.Empty<string>(), OAuthClientTypes.Public);

            Assert.DoesNotContain(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.RefreshToken, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.ClientCredentials, perms);
            Assert.DoesNotContain(OAuthPermissions.ResponseTypes.Code, perms);
        }

        [Fact]
        public void Empty_grant_types_for_confidential_does_NOT_silently_add_client_credentials()
        {
            // Same invariant for Confidential clients: the previous code
            // helpfully added ClientCredentials whenever a confidential
            // client was created with an empty grant list, which silently
            // over-privileged confidential web apps that only ever needed
            // authorization_code. No more — admins pick explicitly.
            var perms = OAuthAdminMapping.BuildClientPermissions(
                Array.Empty<string>(), Array.Empty<string>(), OAuthClientTypes.Confidential);

            Assert.DoesNotContain(OAuthPermissions.GrantTypes.AuthorizationCode, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.RefreshToken, perms);
            Assert.DoesNotContain(OAuthPermissions.GrantTypes.ClientCredentials, perms);
            Assert.DoesNotContain(OAuthPermissions.ResponseTypes.Code, perms);
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
        [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
        public void Maps_grant_type_to_permission_and_back(string grantType)
        {
            var permission = OAuthAdminMapping.MapGrantTypeToPermission(grantType);
            Assert.NotNull(permission);

            var roundTripped = OAuthAdminMapping.MapPermissionToGrantType(permission!);
            Assert.Equal(grantType, roundTripped);
        }

        [Theory]
        [InlineData("implicit")]
        [InlineData("password")]
        public void Implicit_and_password_are_removed_and_map_to_null(string removedGrant)
        {
            // OAuth 2.1 removes both grants. They must not map to a permission, so
            // ValidateGrantTypes rejects them and BuildClientPermissions can't add them.
            Assert.Null(OAuthAdminMapping.MapGrantTypeToPermission(removedGrant));
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

    public class ValidateGrantTypes
    {
        [Theory]
        [InlineData("authorization_code")]
        [InlineData("client_credentials")]
        [InlineData("refresh_token")]
        [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
        [InlineData("urn:cocoar:otp")]
        [InlineData("urn:cocoar:magic")]
        [InlineData("urn:cocoar:passkey")]
        public void Supported_grant_types_are_accepted(string grant)
        {
            Assert.Null(OAuthAdminMapping.ValidateGrantTypes(new[] { grant }));
        }

        [Theory]
        [InlineData("implicit")]
        [InlineData("password")]
        [InlineData("xyz")]
        public void Unsupported_grant_types_are_rejected(string grant)
        {
            var err = OAuthAdminMapping.ValidateGrantTypes(new[] { "authorization_code", grant });
            Assert.NotNull(err);
            Assert.Equal("OAuth.UnsupportedGrantType", err!.Value.Code);
            Assert.Contains(grant, err.Value.Description);
        }

        [Fact]
        public void Null_and_empty_are_valid()
        {
            // A client with no token-flow grants is a legitimate (if useless) state
            // — BuildClientPermissions leaves it unable to mint tokens.
            Assert.Null(OAuthAdminMapping.ValidateGrantTypes(null));
            Assert.Null(OAuthAdminMapping.ValidateGrantTypes(System.Array.Empty<string>()));
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
        public void Always_writes_access_token_type()
        {
            var dto = new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
                AccessTokenType = AccessTokenType.Jwt,
            };

            var settings = OAuthAdminMapping.BuildClientSettings(dto);

            Assert.Equal("Jwt", settings[OAuthApplicationSettingKeys.AccessTokenType]);
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
                SlidingRefreshTokenLifetime = 1296000,
                ClientClaimsPrefix = "client_",
            };

            var settings = OAuthAdminMapping.BuildClientSettings(dto);

            Assert.Equal("300", settings[OAuthApplicationSettingKeys.IdentityTokenLifetime]);
            Assert.Equal("3600", settings[OAuthApplicationSettingKeys.AccessTokenLifetime]);
            Assert.Equal("60", settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime]);
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
        public void Parses_access_token_type_from_settings()
        {
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings =
                {
                    [OAuthApplicationSettingKeys.AccessTokenType] = AccessTokenType.Jwt.ToString(),
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);

            Assert.Equal(AccessTokenType.Jwt, dto.AccessTokenType);
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
                    [OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = "not-a-number",
                },
            };

            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Equal(3600, dto.AccessTokenLifetime);
            Assert.Null(dto.SlidingRefreshTokenLifetime);
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

        [Fact]
        public void LinkedServiceAccountId_round_trips_as_short_guid_when_set()
        {
            var saId = Guid.NewGuid();
            var s = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                LinkedServiceAccountId = saId,
            };

            var dto = OAuthAdminMapping.MapClient(s);

            Assert.Equal(new BuildingBlocks.Helper.ShortGuid(saId).ToString(), dto.LinkedServiceAccountId);
        }

        [Fact]
        public void LinkedServiceAccountId_is_null_on_user_flow_clients()
        {
            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };
            var dto = OAuthAdminMapping.MapClient(s);
            Assert.Null(dto.LinkedServiceAccountId);
        }

        [Fact]
        public void RequireDpop_defaults_false_when_property_missing()
        {
            // Clients created before #118 carry no RequireDpop key — the read
            // must default to "not required" so DPoP stays offered-not-required.
            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };
            Assert.False(OAuthAdminMapping.MapClient(s).RequireDpop);
        }

        [Fact]
        public void RequireDpop_round_trips_true_through_BuildClientProperties()
        {
            var props = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>(),
                requireDpop: true);

            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c", Properties = props };

            Assert.True(OAuthAdminMapping.MapClient(s).RequireDpop);
        }

        [Fact]
        public void RequireDpopNonce_defaults_false_when_property_missing()
        {
            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };
            Assert.False(OAuthAdminMapping.MapClient(s).RequireDpopNonce);
        }

        [Fact]
        public void RequireDpopNonce_round_trips_true_through_BuildClientProperties()
        {
            var props = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>(),
                requireDpop: false, requireDpopNonce: true);

            var s = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c", Properties = props };

            Assert.True(OAuthAdminMapping.MapClient(s).RequireDpopNonce);
            Assert.False(OAuthAdminMapping.MapClient(s).RequireDpop);
        }
    }

    public class ValidateServiceAccountLinkInvariant
    {
        [Fact]
        public void User_flow_only_grants_without_link_is_valid()
        {
            Assert.Null(OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "authorization_code", "refresh_token" }, linkedServiceAccountId: null));
        }

        [Fact]
        public void Empty_grants_without_link_is_valid()
        {
            // An admin-created client with no grants yet picked is a valid
            // intermediate state — BuildClientPermissions explicitly emits
            // no token-flow permissions in that case (pinned elsewhere).
            Assert.Null(OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                Array.Empty<string>(), linkedServiceAccountId: null));
        }

        [Fact]
        public void Client_credentials_without_link_is_rejected_R1()
        {
            var err = OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "client_credentials" }, linkedServiceAccountId: null);
            Assert.NotNull(err);
            Assert.Equal("OAuth.ClientCredentialsRequiresServiceAccountLink", err!.Value.Code);
        }

        [Fact]
        public void Client_credentials_with_link_is_valid()
        {
            Assert.Null(OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "client_credentials" }, linkedServiceAccountId: Guid.NewGuid()));
        }

        [Fact]
        public void Link_with_user_flow_grants_is_rejected_R3()
        {
            var err = OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "client_credentials", "authorization_code" }, linkedServiceAccountId: Guid.NewGuid());
            Assert.NotNull(err);
            Assert.Equal("OAuth.ServiceAccountLinkRequiresClientCredentialsOnly", err!.Value.Code);
        }

        [Fact]
        public void Link_without_client_credentials_is_rejected_R2()
        {
            var err = OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "authorization_code" }, linkedServiceAccountId: Guid.NewGuid());
            Assert.NotNull(err);
            Assert.Equal("OAuth.ServiceAccountLinkRequiresClientCredentialsOnly", err!.Value.Code);
        }

        [Theory]
        [InlineData("authorization_code")]
        [InlineData("refresh_token")]
        [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
        public void All_user_flow_grants_violate_R3_when_link_set(string userFlowGrant)
        {
            var err = OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
                new[] { "client_credentials", userFlowGrant }, linkedServiceAccountId: Guid.NewGuid());
            Assert.NotNull(err);
            Assert.Equal("OAuth.ServiceAccountLinkRequiresClientCredentialsOnly", err!.Value.Code);
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
            // OAUTH-16: secrets are emitted in Base64Url form so they survive
            // form-body / URL-segment usage without re-encoding pitfalls.
            var s = OAuthAdminMapping.GenerateSecret();
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
            Assert.DoesNotContain('=', s);

            // Round-trip via Base64Url to confirm it decodes to 32 bytes.
            var padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var bytes = Convert.FromBase64String(padded);
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

    public class MergeClientSettings
    {
        // Partial-PATCH semantics: a setting absent from the DTO must be
        // preserved from `current`; a setting present must overwrite. Critical
        // because every UpdateClientAsync call routes through here — getting
        // this wrong silently wipes lifetimes/token-types on every patch that
        // doesn't re-include them.

        [Fact]
        public void Empty_dto_returns_a_copy_with_every_existing_setting_preserved()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenType] = "Reference",
                [OAuthApplicationSettingKeys.AccessTokenLifetime] = "300",
                [OAuthApplicationSettingKeys.ClientClaimsPrefix] = "client_",
            };
            var dto = new UpdateOAuthClientDto();

            var merged = OAuthAdminMapping.MergeClientSettings(current, dto);

            Assert.Equal(current.Count, merged.Count);
            foreach (var kv in current)
                Assert.Equal(kv.Value, merged[kv.Key]);
        }

        [Fact]
        public void Returns_fresh_dictionary_so_caller_can_compare_with_DictEquals()
        {
            // Pin: never returns the same reference as `current`. The caller
            // relies on this to compare new-vs-old and decide whether to emit
            // a change event.
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenType] = "Reference",
            };
            var merged = OAuthAdminMapping.MergeClientSettings(current, new UpdateOAuthClientDto());

            Assert.NotSame(current, merged);
        }

        [Fact]
        public void Does_not_mutate_the_current_dictionary()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenType] = "Reference",
            };
            var dto = new UpdateOAuthClientDto { AccessTokenType = AccessTokenType.Jwt };

            _ = OAuthAdminMapping.MergeClientSettings(current, dto);

            Assert.Equal("Reference", current[OAuthApplicationSettingKeys.AccessTokenType]);
        }

        [Fact]
        public void DTO_AccessTokenType_overwrites_current()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenType] = "Reference",
            };
            var dto = new UpdateOAuthClientDto { AccessTokenType = AccessTokenType.Jwt };

            var merged = OAuthAdminMapping.MergeClientSettings(current, dto);

            Assert.Equal("Jwt", merged[OAuthApplicationSettingKeys.AccessTokenType]);
        }

        [Fact]
        public void Numeric_lifetime_settings_stringify_through_default_ToString()
        {
            // Pinning: invariant decimal. If a future regression starts using a
            // culture-aware ToString(), values written in de-DE could come out
            // with thousand separators or non-ASCII digits and break the
            // OpenIddict-runtime parser on the read side.
            var dto = new UpdateOAuthClientDto
            {
                AccessTokenLifetime = 12345,
                IdentityTokenLifetime = 600,
            };

            var merged = OAuthAdminMapping.MergeClientSettings(
                new Dictionary<string, string>(), dto);

            Assert.Equal("12345", merged[OAuthApplicationSettingKeys.AccessTokenLifetime]);
            Assert.Equal("600", merged[OAuthApplicationSettingKeys.IdentityTokenLifetime]);
        }

        [Fact]
        public void Setting_a_lifetime_only_writes_that_one_key()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenLifetime] = "300",
                [OAuthApplicationSettingKeys.IdentityTokenLifetime] = "600",
            };
            var dto = new UpdateOAuthClientDto { AccessTokenLifetime = 900 };

            var merged = OAuthAdminMapping.MergeClientSettings(current, dto);

            Assert.Equal("900", merged[OAuthApplicationSettingKeys.AccessTokenLifetime]);
            Assert.Equal("600", merged[OAuthApplicationSettingKeys.IdentityTokenLifetime]);
        }

        [Fact]
        public void ClientClaimsPrefix_treats_null_as_omitted_and_string_as_overwrite()
        {
            // Note: the DTO uses `string?` for prefix, not Optional/HasValue.
            // Currently there's no way to clear an existing prefix via PATCH;
            // null means "absent". Pin this so an "intentional clear" feature
            // request later goes through a deliberate behaviour change.
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.ClientClaimsPrefix] = "old_",
            };

            var keepDto = new UpdateOAuthClientDto { ClientClaimsPrefix = null };
            var keepMerged = OAuthAdminMapping.MergeClientSettings(current, keepDto);
            Assert.Equal("old_", keepMerged[OAuthApplicationSettingKeys.ClientClaimsPrefix]);

            var setDto = new UpdateOAuthClientDto { ClientClaimsPrefix = "new_" };
            var setMerged = OAuthAdminMapping.MergeClientSettings(current, setDto);
            Assert.Equal("new_", setMerged[OAuthApplicationSettingKeys.ClientClaimsPrefix]);
        }

        [Fact]
        public void Result_round_trips_through_DictEquals_against_unchanged_input()
        {
            // No-op patch + DictEquals = false-positive risk if the helper
            // ever introduces ordering or extra keys silently. Pin: an empty
            // dto produces a dict that DictEquals reports as equal.
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.AccessTokenType] = "Reference",
                [OAuthApplicationSettingKeys.AccessTokenLifetime] = "300",
            };
            var merged = OAuthAdminMapping.MergeClientSettings(current, new UpdateOAuthClientDto());

            Assert.True(OAuthAdminMapping.DictEquals(current, merged));
        }
    }

    // ─────────────── Native token-lifetime wiring (issue #115) ─────────────────

    public class ApplyNativeTokenLifetimes
    {
        [Fact]
        public void All_null_is_a_no_op()
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, null, null, null);

            Assert.Null(err);
            Assert.Empty(settings);
        }

        [Fact]
        public void Valid_values_write_the_openiddict_native_keys_as_timespan_c_format()
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings,
                identityTokenLifetimeSeconds: 300,
                accessTokenLifetimeSeconds: 1800,
                authorizationCodeLifetimeSeconds: 300,
                slidingRefreshTokenLifetimeSeconds: 86400);

            Assert.Null(err);
            Assert.Equal("00:05:00", settings[OAuthAdminMapping.OpenIddictIdentityTokenLifetimeSettingKey]);
            Assert.Equal("00:30:00", settings[OAuthAdminMapping.OpenIddictAccessTokenLifetimeSettingKey]);
            Assert.Equal("00:05:00", settings[OAuthAdminMapping.OpenIddictAuthorizationCodeLifetimeSettingKey]);
            Assert.Equal("1.00:00:00", settings[OAuthAdminMapping.OpenIddictRefreshTokenLifetimeSettingKey]);
        }

        [Fact]
        public void Boundary_values_are_accepted_1_minute_and_60_minutes_access()
        {
            var settings = new Dictionary<string, string>();

            Assert.Null(OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, 60, null, null));
            Assert.Null(OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, 3600, null, null));
        }

        [Theory]
        [InlineData(59)]   // just under 1 minute
        [InlineData(3601)] // just over 60 minutes
        public void AccessTokenLifetime_outside_1_to_60_minutes_is_rejected(int seconds)
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings, identityTokenLifetimeSeconds: null, accessTokenLifetimeSeconds: seconds,
                authorizationCodeLifetimeSeconds: null, slidingRefreshTokenLifetimeSeconds: null);

            Assert.NotNull(err);
            Assert.Equal("OAuthClient.InvalidAccessTokenLifetime", err!.Value.Code);
            Assert.False(settings.ContainsKey(OAuthAdminMapping.OpenIddictAccessTokenLifetimeSettingKey));
        }

        [Theory]
        [InlineData(59)]
        [InlineData(3601)]
        public void IdentityTokenLifetime_outside_1_to_60_minutes_is_rejected(int seconds)
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings, identityTokenLifetimeSeconds: seconds, accessTokenLifetimeSeconds: null,
                authorizationCodeLifetimeSeconds: null, slidingRefreshTokenLifetimeSeconds: null);

            Assert.NotNull(err);
            Assert.Equal("OAuthClient.InvalidIdentityTokenLifetime", err!.Value.Code);
            Assert.False(settings.ContainsKey(OAuthAdminMapping.OpenIddictIdentityTokenLifetimeSettingKey));
        }

        [Fact]
        public void Boundary_values_are_accepted_1_minute_and_10_minutes_authorization_code()
        {
            var settings = new Dictionary<string, string>();

            Assert.Null(OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, null, 60, null));
            Assert.Null(OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, null, 600, null));
        }

        [Theory]
        [InlineData(59)]  // just under 1 minute
        [InlineData(601)] // just over 10 minutes
        public void AuthorizationCodeLifetime_outside_1_to_10_minutes_is_rejected(int seconds)
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings, identityTokenLifetimeSeconds: null, accessTokenLifetimeSeconds: null,
                authorizationCodeLifetimeSeconds: seconds, slidingRefreshTokenLifetimeSeconds: null);

            Assert.NotNull(err);
            Assert.Equal("OAuthClient.InvalidAuthorizationCodeLifetime", err!.Value.Code);
            Assert.False(settings.ContainsKey(OAuthAdminMapping.OpenIddictAuthorizationCodeLifetimeSettingKey));
        }

        [Theory]
        [InlineData(86399)]   // just under 1 day
        [InlineData(2592001)] // just over 30 days
        public void SlidingRefreshTokenLifetime_outside_1_to_30_days_is_rejected(int seconds)
        {
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings, identityTokenLifetimeSeconds: null, accessTokenLifetimeSeconds: null,
                authorizationCodeLifetimeSeconds: null, slidingRefreshTokenLifetimeSeconds: seconds);

            Assert.NotNull(err);
            Assert.Equal("OAuthClient.InvalidSlidingRefreshTokenLifetime", err!.Value.Code);
            Assert.False(settings.ContainsKey(OAuthAdminMapping.OpenIddictRefreshTokenLifetimeSettingKey));
        }

        [Fact]
        public void Rejection_of_a_later_field_does_not_leave_earlier_fields_partially_written()
        {
            // Access + Identity + AuthorizationCode are individually valid, but
            // the refresh value is out of range — the whole call must be atomic
            // w.r.t. `settings` so a 400 response never corresponds to a
            // half-applied write.
            var settings = new Dictionary<string, string>();

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(
                settings,
                identityTokenLifetimeSeconds: 300,
                accessTokenLifetimeSeconds: 1800,
                authorizationCodeLifetimeSeconds: 300,
                slidingRefreshTokenLifetimeSeconds: 1); // way below the 1-day floor

            Assert.NotNull(err);
            Assert.Empty(settings);
        }

        [Fact]
        public void Existing_key_is_left_untouched_when_the_corresponding_field_is_null()
        {
            // PATCH semantics: omitted field preserves whatever tkn_lft:* value
            // (e.g. from a prior save, or a DCR-realm override) is already there.
            var settings = new Dictionary<string, string>
            {
                [OAuthAdminMapping.OpenIddictAccessTokenLifetimeSettingKey] = "0.00:10:00",
            };

            var err = OAuthAdminMapping.ApplyNativeTokenLifetimes(settings, null, null, null, null);

            Assert.Null(err);
            Assert.Equal("0.00:10:00", settings[OAuthAdminMapping.OpenIddictAccessTokenLifetimeSettingKey]);
        }
    }

    public class MergeClientProperties
    {
        // The Properties bag is the most-edited part of a client and the place
        // where partial-PATCH semantics matter most. Wrong merge → silent
        // disable / silent re-enable / silent role removal. Tests pin: omitted
        // = preserve, present = overwrite, defaults match the legacy IdP.

        [Fact]
        public void Empty_dto_against_empty_current_yields_legacy_default_values()
        {
            var merged = OAuthAdminMapping.MergeClientProperties(
                new Dictionary<string, object?>(), new UpdateOAuthClientDto());

            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.Enabled, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireClientSecret, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.EnableLocalLogin, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AllowRememberConsent, false));

            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, true));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireConsent, true));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, true));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, true));

            Assert.Empty(OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.AllowedCorsOrigins));
            Assert.Empty(OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.Roles));
        }

        [Fact]
        public void Empty_dto_preserves_every_value_from_current_through_round_trip()
        {
            // Setup: a fully-populated current bag with NON-default values.
            // After the empty-dto merge each must come back unchanged.
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: false,
                allowBrowser: true,
                requireSecret: false,
                enableLocal: false,
                requireConsent: true,
                allowRemember: false,
                corsOrigins: new[] { "https://app.example.com" },
                alwaysSend: true,
                updateClaims: true,
                claims: Array.Empty<OAuthClientClaimDto>(),
                roles: new[] { "admin", "user" });

            var merged = OAuthAdminMapping.MergeClientProperties(current, new UpdateOAuthClientDto());

            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.Enabled, true));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireClientSecret, true));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.EnableLocalLogin, true));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireConsent, false));
            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AllowRememberConsent, true));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false));

            Assert.Equal(new[] { "https://app.example.com" },
                OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.AllowedCorsOrigins));
            Assert.Equal(new[] { "admin", "user" },
                OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.Roles));
        }

        [Fact]
        public void Bool_dto_override_replaces_only_that_field_others_preserved()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>());

            var dto = new UpdateOAuthClientDto { Enabled = false };

            var merged = OAuthAdminMapping.MergeClientProperties(current, dto);

            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.Enabled, true));
            // Untouched fields keep their values:
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireClientSecret, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.EnableLocalLogin, false));
            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.AllowRememberConsent, false));
        }

        [Fact]
        public void Roles_list_overwrite_replaces_the_whole_list_does_not_concat()
        {
            // Pin the contract: lists are replace-not-merge. A user who wants
            // to keep some roles must include them in the patch.
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(),
                roles: new[] { "old-role" });

            var dto = new UpdateOAuthClientDto { Roles = new() { "new-role" } };

            var merged = OAuthAdminMapping.MergeClientProperties(current, dto);

            Assert.Equal(new[] { "new-role" },
                OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.Roles));
        }

        [Fact]
        public void Empty_list_in_dto_clears_existing_list()
        {
            // Distinguish "null = preserve" from "[] = clear". An admin
            // explicitly sending [] is removing every value.
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true,
                corsOrigins: new[] { "https://old.example.com" },
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>());

            var dto = new UpdateOAuthClientDto { AllowedCorsOrigins = new() };

            var merged = OAuthAdminMapping.MergeClientProperties(current, dto);

            Assert.Empty(OAuthAdminMapping.GetStringListProp(merged, OAuthApplicationPropertyKeys.AllowedCorsOrigins));
        }

        [Fact]
        public void Claims_list_overwrite_replaces_the_whole_list()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: new[] { new OAuthClientClaimDto { Type = "old", Value = "x" } },
                roles: Array.Empty<string>());

            var dto = new UpdateOAuthClientDto
            {
                Claims = new() { new OAuthClientClaimDto { Type = "new", Value = "y" } },
            };

            var merged = OAuthAdminMapping.MergeClientProperties(current, dto);

            var claims = OAuthAdminMapping.GetClaimsProp(merged);
            var c = Assert.Single(claims);
            Assert.Equal("new", c.Type);
            Assert.Equal("y", c.Value);
        }

        [Fact]
        public void Returns_fresh_dictionary_so_caller_cannot_observe_aliasing()
        {
            var current = new Dictionary<string, object?>();
            var merged = OAuthAdminMapping.MergeClientProperties(current, new UpdateOAuthClientDto());

            Assert.NotSame(current, merged);
        }

        [Fact]
        public void Does_not_mutate_the_current_dictionary()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>());
            var snapshotKeys = current.Keys.ToHashSet();

            _ = OAuthAdminMapping.MergeClientProperties(current,
                new UpdateOAuthClientDto { Enabled = false, Roles = new() { "x" } });

            Assert.True(OAuthAdminMapping.GetBoolProp(current, OAuthApplicationPropertyKeys.Enabled, false));
            Assert.True(snapshotKeys.SetEquals(current.Keys));
        }

        // RFC 9449 (#118) — RequireDpop follows the same null=preserve / value=set
        // PATCH contract as the other booleans.

        [Fact]
        public void RequireDpop_null_patch_preserves_existing_true()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>(),
                requireDpop: true);

            var merged = OAuthAdminMapping.MergeClientProperties(current, new UpdateOAuthClientDto());

            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireDpop, false));
        }

        [Fact]
        public void RequireDpop_false_patch_overrides_existing_true()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>(),
                requireDpop: true);

            var merged = OAuthAdminMapping.MergeClientProperties(
                current, new UpdateOAuthClientDto { RequireDpop = false });

            Assert.False(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireDpop, true));
        }

        [Fact]
        public void RequireDpop_true_patch_sets_it_from_default()
        {
            var merged = OAuthAdminMapping.MergeClientProperties(
                new Dictionary<string, object?>(), new UpdateOAuthClientDto { RequireDpop = true });

            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireDpop, false));
        }

        [Fact]
        public void RequireDpopNonce_null_patch_preserves_existing_true()
        {
            var current = OAuthAdminMapping.BuildClientProperties(
                enabled: true, allowBrowser: false, requireSecret: true, enableLocal: true,
                requireConsent: false, allowRemember: true, corsOrigins: Array.Empty<string>(),
                alwaysSend: false, updateClaims: false,
                claims: Array.Empty<OAuthClientClaimDto>(), roles: Array.Empty<string>(),
                requireDpop: false, requireDpopNonce: true);

            var merged = OAuthAdminMapping.MergeClientProperties(current, new UpdateOAuthClientDto());

            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireDpopNonce, false));
        }

        [Fact]
        public void RequireDpopNonce_true_patch_sets_it_from_default()
        {
            var merged = OAuthAdminMapping.MergeClientProperties(
                new Dictionary<string, object?>(), new UpdateOAuthClientDto { RequireDpopNonce = true });

            Assert.True(OAuthAdminMapping.GetBoolProp(merged, OAuthApplicationPropertyKeys.RequireDpopNonce, false));
        }
    }

    public class MapApiState
    {
        private static Modgud.Domain.OAuth.Apis.OAuthApiState SampleState() => new()
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Name = "billing-api",
            DisplayName = "Billing API",
            Description = "Charges and invoices",
            Enabled = true,
            Scopes = new() { "billing:read", "billing:write" },
            UserClaims = new() { "sub", "email" },
        };

        [Fact]
        public void Maps_every_state_field_into_dto_with_id_stringified()
        {
            var dto = OAuthAdminMapping.MapApiState(SampleState());

            Assert.Equal("11111111-2222-3333-4444-555555555555", dto.Id);
            Assert.Equal("billing-api", dto.Name);
            Assert.Equal("Billing API", dto.DisplayName);
            Assert.Equal("Charges and invoices", dto.Description);
            Assert.True(dto.Enabled);
            Assert.Equal(new[] { "billing:read", "billing:write" }, dto.Scopes);
            Assert.Equal(new[] { "sub", "email" }, dto.UserClaims);
        }

        [Fact]
        public void Disabled_state_passes_through_to_dto()
        {
            var state = SampleState();
            state.Enabled = false;

            var dto = OAuthAdminMapping.MapApiState(state);

            Assert.False(dto.Enabled);
        }

        [Fact]
        public void Defensive_copies_state_collections_so_caller_mutations_dont_bleed()
        {
            // The DTO is the response — the upstream state document continues
            // to live in the projection cache. A later mutation of the state's
            // Scopes list must not retroactively change a previously-handed-out DTO.
            var state = SampleState();
            var dto = OAuthAdminMapping.MapApiState(state);

            state.Scopes.Add("newly-added-scope");
            state.UserClaims.Add("newly-added-claim");

            Assert.DoesNotContain("newly-added-scope", dto.Scopes);
            Assert.DoesNotContain("newly-added-claim", dto.UserClaims);
        }
    }

    // ─────────────────── ADR-0009 per-client WebAuthn RP-ID ────────────────────

    public class WebAuthnRpIdValidation
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("app.example.com")]
        [InlineData("amzettel.at")]
        [InlineData("localhost")]
        [InlineData("a.localhost")]
        public void Accepts_null_blank_or_bare_hostname(string? value)
            => Assert.Null(OAuthAdminMapping.ValidateWebAuthnRpId(value));

        [Theory]
        [InlineData("https://app.example.com")] // scheme
        [InlineData("app.example.com/path")]    // path
        [InlineData("app.example.com:443")]     // port
        [InlineData("app example com")]          // whitespace
        [InlineData("127.0.0.1")]                // IP, not a DNS host
        public void Rejects_url_port_path_whitespace_or_ip(string value)
        {
            var err = OAuthAdminMapping.ValidateWebAuthnRpId(value);
            Assert.NotNull(err);
            Assert.Equal("OAuth.InvalidWebAuthnRpId", err!.Value.Code);
        }
    }

    public class WebAuthnRpIdMapping
    {
        [Fact]
        public void BuildClientSettings_stores_normalized_value()
        {
            var settings = OAuthAdminMapping.BuildClientSettings(new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
                WebAuthnRpId = "  App.Example.COM  ",
            });

            Assert.Equal("app.example.com", settings[OAuthApplicationSettingKeys.WebAuthnRpId]);
        }

        [Fact]
        public void BuildClientSettings_omits_key_when_blank()
        {
            var settings = OAuthAdminMapping.BuildClientSettings(new CreateOAuthClientDto
            {
                ClientId = "c",
                ClientType = OAuthClientTypes.Public,
                WebAuthnRpId = "   ",
            });

            Assert.DoesNotContain(OAuthApplicationSettingKeys.WebAuthnRpId, settings.Keys);
        }

        [Fact]
        public void MergeClientSettings_null_preserves_existing()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.WebAuthnRpId] = "app.example.com",
            };

            var merged = OAuthAdminMapping.MergeClientSettings(current, new UpdateOAuthClientDto { WebAuthnRpId = null });

            Assert.Equal("app.example.com", merged[OAuthApplicationSettingKeys.WebAuthnRpId]);
        }

        [Fact]
        public void MergeClientSettings_empty_clears_back_to_realm_scoped()
        {
            var current = new Dictionary<string, string>
            {
                [OAuthApplicationSettingKeys.WebAuthnRpId] = "app.example.com",
            };

            var merged = OAuthAdminMapping.MergeClientSettings(current, new UpdateOAuthClientDto { WebAuthnRpId = "" });

            Assert.DoesNotContain(OAuthApplicationSettingKeys.WebAuthnRpId, merged.Keys);
        }

        [Fact]
        public void MergeClientSettings_value_sets_normalized()
        {
            var merged = OAuthAdminMapping.MergeClientSettings(
                new Dictionary<string, string>(), new UpdateOAuthClientDto { WebAuthnRpId = "B.Localhost" });

            Assert.Equal("b.localhost", merged[OAuthApplicationSettingKeys.WebAuthnRpId]);
        }

        [Fact]
        public void MapClient_reads_back_the_rp_id()
        {
            var state = new OAuthApplicationState
            {
                Id = Guid.NewGuid(),
                ClientId = "c",
                Settings = new Dictionary<string, string>
                {
                    [OAuthApplicationSettingKeys.WebAuthnRpId] = "app.example.com",
                },
            };

            var dto = OAuthAdminMapping.MapClient(state);

            Assert.Equal("app.example.com", dto.WebAuthnRpId);
        }

        [Fact]
        public void MapClient_null_when_unset()
        {
            var state = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "c" };

            var dto = OAuthAdminMapping.MapClient(state);

            Assert.Null(dto.WebAuthnRpId);
        }
    }
}
