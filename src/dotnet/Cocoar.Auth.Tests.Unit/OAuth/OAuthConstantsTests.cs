using Cocoar.Auth.Domain.OAuth.Common;

namespace Cocoar.Auth.Tests.Unit.OAuth;

/// <summary>
/// Pins the wire-format constants used to build OpenIddict permission strings, client types,
/// and consent types. These values are persisted on OpenIddict application rows and are
/// matched verbatim at runtime by OpenIddict's authorization pipeline — drift breaks every
/// OAuth flow for clients that already have those rows in the database.
///
/// One assertion per constant on purpose: any value change should fail loudly, file-by-file,
/// rather than be hidden inside a "smart" parameterised test.
/// </summary>
public class OAuthConstantsTests
{
    public class PermissionPrefixes
    {
        [Fact]
        public void Scope_prefix_is_scp_colon() =>
            Assert.Equal("scp:", OAuthPermissions.Prefixes.Scope);

        [Fact]
        public void GrantType_prefix_is_gt_colon() =>
            Assert.Equal("gt:", OAuthPermissions.Prefixes.GrantType);

        [Fact]
        public void ResponseType_prefix_is_rst_colon() =>
            Assert.Equal("rst:", OAuthPermissions.Prefixes.ResponseType);

        [Fact]
        public void Endpoint_prefix_is_ept_colon() =>
            Assert.Equal("ept:", OAuthPermissions.Prefixes.Endpoint);

        [Fact]
        public void All_prefixes_end_with_colon()
        {
            // OpenIddict's permission scheme uses "<prefix>:<value>" — a missing trailing
            // colon would silently produce permissions like "scpopenid" that no one matches.
            Assert.EndsWith(":", OAuthPermissions.Prefixes.Scope);
            Assert.EndsWith(":", OAuthPermissions.Prefixes.GrantType);
            Assert.EndsWith(":", OAuthPermissions.Prefixes.ResponseType);
            Assert.EndsWith(":", OAuthPermissions.Prefixes.Endpoint);
        }

        [Fact]
        public void All_prefixes_are_unique()
        {
            var prefixes = new[]
            {
                OAuthPermissions.Prefixes.Scope,
                OAuthPermissions.Prefixes.GrantType,
                OAuthPermissions.Prefixes.ResponseType,
                OAuthPermissions.Prefixes.Endpoint,
            };
            Assert.Equal(prefixes.Length, prefixes.Distinct().Count());
        }
    }

    public class Endpoints
    {
        [Fact]
        public void Authorization_value_is_pinned() =>
            Assert.Equal("ept:authorization", OAuthPermissions.Endpoints.Authorization);

        [Fact]
        public void Token_value_is_pinned() =>
            Assert.Equal("ept:token", OAuthPermissions.Endpoints.Token);

        [Fact]
        public void EndSession_value_is_pinned_to_logout()
        {
            // OpenIddict historically named this endpoint "logout"; the OIDC spec calls
            // it "end_session". The constant uses the OpenIddict name — pin it so a
            // well-meaning rename doesn't break existing client rows.
            Assert.Equal("ept:logout", OAuthPermissions.Endpoints.EndSession);
        }

        [Fact]
        public void Introspection_value_is_pinned() =>
            Assert.Equal("ept:introspection", OAuthPermissions.Endpoints.Introspection);

        [Fact]
        public void Revocation_value_is_pinned() =>
            Assert.Equal("ept:revocation", OAuthPermissions.Endpoints.Revocation);

        [Fact]
        public void DeviceAuthorization_value_is_pinned() =>
            Assert.Equal("ept:device_authorization", OAuthPermissions.Endpoints.DeviceAuthorization);

        [Fact]
        public void All_endpoint_constants_use_the_endpoint_prefix()
        {
            var endpoints = new[]
            {
                OAuthPermissions.Endpoints.Authorization,
                OAuthPermissions.Endpoints.Token,
                OAuthPermissions.Endpoints.EndSession,
                OAuthPermissions.Endpoints.Introspection,
                OAuthPermissions.Endpoints.Revocation,
                OAuthPermissions.Endpoints.DeviceAuthorization,
            };

            foreach (var e in endpoints)
                Assert.StartsWith(OAuthPermissions.Prefixes.Endpoint, e);
        }

        [Fact]
        public void All_endpoint_values_are_unique()
        {
            var endpoints = new[]
            {
                OAuthPermissions.Endpoints.Authorization,
                OAuthPermissions.Endpoints.Token,
                OAuthPermissions.Endpoints.EndSession,
                OAuthPermissions.Endpoints.Introspection,
                OAuthPermissions.Endpoints.Revocation,
                OAuthPermissions.Endpoints.DeviceAuthorization,
            };
            Assert.Equal(endpoints.Length, endpoints.Distinct().Count());
        }
    }

    public class GrantTypes
    {
        [Fact]
        public void AuthorizationCode_value_is_pinned() =>
            Assert.Equal("gt:authorization_code", OAuthPermissions.GrantTypes.AuthorizationCode);

        [Fact]
        public void ClientCredentials_value_is_pinned() =>
            Assert.Equal("gt:client_credentials", OAuthPermissions.GrantTypes.ClientCredentials);

        [Fact]
        public void RefreshToken_value_is_pinned() =>
            Assert.Equal("gt:refresh_token", OAuthPermissions.GrantTypes.RefreshToken);

        [Fact]
        public void Implicit_value_is_pinned() =>
            Assert.Equal("gt:implicit", OAuthPermissions.GrantTypes.Implicit);

        [Fact]
        public void Password_value_is_pinned() =>
            Assert.Equal("gt:password", OAuthPermissions.GrantTypes.Password);

        [Fact]
        public void DeviceCode_value_is_the_full_oauth_urn()
        {
            // RFC 8628 specifies the device-code grant type as a URN, not a short alias.
            // Check the full literal rather than just "starts with gt:" so a typo in the URN
            // (which clients would not match) trips immediately.
            Assert.Equal("gt:urn:ietf:params:oauth:grant-type:device_code", OAuthPermissions.GrantTypes.DeviceCode);
        }

        [Fact]
        public void All_grant_type_constants_use_the_grant_type_prefix()
        {
            var grants = new[]
            {
                OAuthPermissions.GrantTypes.AuthorizationCode,
                OAuthPermissions.GrantTypes.ClientCredentials,
                OAuthPermissions.GrantTypes.RefreshToken,
                OAuthPermissions.GrantTypes.Implicit,
                OAuthPermissions.GrantTypes.Password,
                OAuthPermissions.GrantTypes.DeviceCode,
            };

            foreach (var g in grants)
                Assert.StartsWith(OAuthPermissions.Prefixes.GrantType, g);
        }

        [Fact]
        public void All_grant_type_values_are_unique()
        {
            var grants = new[]
            {
                OAuthPermissions.GrantTypes.AuthorizationCode,
                OAuthPermissions.GrantTypes.ClientCredentials,
                OAuthPermissions.GrantTypes.RefreshToken,
                OAuthPermissions.GrantTypes.Implicit,
                OAuthPermissions.GrantTypes.Password,
                OAuthPermissions.GrantTypes.DeviceCode,
            };
            Assert.Equal(grants.Length, grants.Distinct().Count());
        }
    }

    public class ResponseTypes
    {
        [Fact]
        public void Code_value_is_pinned() =>
            Assert.Equal("rst:code", OAuthPermissions.ResponseTypes.Code);

        [Fact]
        public void Code_starts_with_response_type_prefix() =>
            Assert.StartsWith(OAuthPermissions.Prefixes.ResponseType, OAuthPermissions.ResponseTypes.Code);
    }

    public class ClientTypes
    {
        [Fact]
        public void Public_value_is_lowercase_public() =>
            Assert.Equal("public", OAuthClientTypes.Public);

        [Fact]
        public void Confidential_value_is_lowercase_confidential() =>
            Assert.Equal("confidential", OAuthClientTypes.Confidential);

        [Fact]
        public void Public_and_Confidential_are_distinct() =>
            Assert.NotEqual(OAuthClientTypes.Public, OAuthClientTypes.Confidential);
    }

    public class ConsentTypes
    {
        [Fact]
        public void Explicit_value_is_pinned() =>
            Assert.Equal("explicit", OAuthConsentTypes.Explicit);

        [Fact]
        public void Implicit_value_is_pinned() =>
            Assert.Equal("implicit", OAuthConsentTypes.Implicit);

        [Fact]
        public void External_value_is_pinned() =>
            Assert.Equal("external", OAuthConsentTypes.External);

        [Fact]
        public void Systematic_value_is_pinned() =>
            Assert.Equal("systematic", OAuthConsentTypes.Systematic);

        [Fact]
        public void All_consent_type_values_are_unique()
        {
            var consents = new[]
            {
                OAuthConsentTypes.Explicit,
                OAuthConsentTypes.Implicit,
                OAuthConsentTypes.External,
                OAuthConsentTypes.Systematic,
            };
            Assert.Equal(consents.Length, consents.Distinct().Count());
        }
    }
}
