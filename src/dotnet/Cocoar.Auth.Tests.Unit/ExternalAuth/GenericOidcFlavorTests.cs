using System.Text.Json;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth.Flavors;

namespace Cocoar.Auth.Tests.Unit.ExternalAuth;

/// <summary>
/// Pins the Generic-OIDC fallback flavor. It accepts an arbitrary discovery URL
/// and infers an authority by trimming the standard well-known suffix when
/// present — the convention OIDC handlers downstream rely on.
/// </summary>
public class GenericOidcFlavorTests
{
    private static JsonDocument Json(string raw) => JsonDocument.Parse(raw);

    public class Identity
    {
        [Fact]
        public void Key_is_the_canonical_GenericOidc_constant()
        {
            var flavor = new GenericOidcFlavor();
            Assert.Equal(IdpFlavor.GenericOidc, flavor.Key);
            Assert.Equal("GenericOidc", flavor.Key);
        }

        [Fact]
        public void Display_name_and_icon_are_stable()
        {
            var flavor = new GenericOidcFlavor();
            Assert.Equal("Generic OIDC", flavor.DisplayName);
            Assert.Equal("key-round", flavor.DefaultIconName);
        }

        [Fact]
        public void Default_scopes_cover_openid_profile_email()
        {
            var flavor = new GenericOidcFlavor();
            Assert.Equal(new[] { "openid", "profile", "email" }, flavor.DefaultScopes);
        }

        [Fact]
        public void Defaults_to_storing_raw_claims()
        {
            // Generic-OIDC is unknown territory; raw claims help admins map
            // unfamiliar IdPs.
            Assert.True(new GenericOidcFlavor().DefaultStoreRawClaims);
        }

        [Fact]
        public void Default_user_update_script_is_a_claims_arrow_function()
        {
            var script = new GenericOidcFlavor().DefaultUserUpdateScript;
            Assert.False(string.IsNullOrWhiteSpace(script));
            Assert.Contains("(claims) =>", script);
            Assert.Contains("firstname", script);
            Assert.Contains("lastname", script);
            Assert.Contains("email", script);
            Assert.Contains("acronym", script);
        }
    }

    public class ConfigSchemaShape
    {
        [Fact]
        public void Exposes_only_the_MetadataUri_field()
        {
            var schema = new GenericOidcFlavor().ConfigSchema;
            Assert.Single(schema);
            Assert.Equal("MetadataUri", schema[0].Key);
        }

        [Fact]
        public void MetadataUri_field_is_a_required_url_with_help_text()
        {
            var field = new GenericOidcFlavor().ConfigSchema.Single(f => f.Key == "MetadataUri");
            Assert.Equal(FlavorConfigFieldType.Url, field.Type);
            Assert.True(field.Required);
            Assert.Equal("Discovery URL", field.Label);
            Assert.False(string.IsNullOrWhiteSpace(field.HelpText));
            Assert.False(string.IsNullOrWhiteSpace(field.Placeholder));
        }
    }

    public class DeriveEndpoints
    {
        [Fact]
        public void Strips_well_known_suffix_to_produce_authority()
        {
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://idp.example.com/.well-known/openid-configuration"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://idp.example.com", endpoints.Authority);
            Assert.Equal("https://idp.example.com/.well-known/openid-configuration", endpoints.MetadataUri);
        }

        [Fact]
        public void Strips_well_known_suffix_case_insensitively()
        {
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://idp.example.com/.WELL-KNOWN/OpenID-Configuration"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://idp.example.com", endpoints.Authority);
        }

        [Fact]
        public void Preserves_realm_path_when_stripping_suffix()
        {
            // Keycloak-style: discovery lives under the realm.
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://kc.example.com/realms/acme/.well-known/openid-configuration"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://kc.example.com/realms/acme", endpoints.Authority);
        }

        [Fact]
        public void Falls_back_to_metadata_uri_as_authority_when_suffix_absent()
        {
            // No well-known suffix — flavor cannot infer a cleaner authority and
            // hands the metadata URI back as-is. Documented behavior, not ideal,
            // but the OIDC handler uses MetadataUri for discovery anyway.
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://idp.example.com/oidc"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://idp.example.com/oidc", endpoints.Authority);
            Assert.Equal("https://idp.example.com/oidc", endpoints.MetadataUri);
        }

        [Fact]
        public void Trailing_slash_before_well_known_blocks_suffix_strip()
        {
            // Documents the literal suffix-match: a trailing slash before
            // ".well-known/..." prevents the strip and the authority falls back
            // to the metadata URI verbatim. Discovery still works because the
            // OIDC handler uses MetadataUri directly.
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://idp.example.com//.well-known/openid-configuration"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://idp.example.com/", endpoints.Authority);
            Assert.Equal("https://idp.example.com//.well-known/openid-configuration", endpoints.MetadataUri);
        }

        [Fact]
        public void Leaves_explicit_endpoints_unset_so_OIDC_handler_uses_discovery()
        {
            var flavor = new GenericOidcFlavor();
            using var doc = Json("""{"MetadataUri":"https://idp.example.com/.well-known/openid-configuration"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Null(endpoints.AuthorizationEndpoint);
            Assert.Null(endpoints.TokenEndpoint);
            Assert.Null(endpoints.UserInfoEndpoint);
            Assert.Null(endpoints.EndSessionEndpoint);
        }

        [Fact]
        public void Throws_when_flavor_data_is_null()
        {
            var ex = Assert.Throws<ArgumentException>(() => new GenericOidcFlavor().DeriveEndpoints(null));
            Assert.Equal("flavorData", ex.ParamName);
        }

        [Fact]
        public void Throws_when_metadata_uri_property_is_missing()
        {
            using var doc = Json("""{"OtherProp":"value"}""");
            Assert.Throws<ArgumentException>(() => new GenericOidcFlavor().DeriveEndpoints(doc));
        }

        [Fact]
        public void Throws_when_metadata_uri_is_empty_or_whitespace()
        {
            using var empty = Json("""{"MetadataUri":""}""");
            using var blanks = Json("""{"MetadataUri":"   "}""");

            Assert.Throws<ArgumentException>(() => new GenericOidcFlavor().DeriveEndpoints(empty));
            Assert.Throws<ArgumentException>(() => new GenericOidcFlavor().DeriveEndpoints(blanks));
        }

        [Fact]
        public void Throws_when_metadata_uri_is_not_a_string()
        {
            using var doc = Json("""{"MetadataUri":42}""");
            Assert.Throws<ArgumentException>(() => new GenericOidcFlavor().DeriveEndpoints(doc));
        }
    }
}
