using System.Text.Json;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Flavors;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// Pins the Entra ID flavor's identity, defaults, and endpoint-derivation logic.
/// Authority/metadata URLs are stored on persisted IdP configurations and
/// consumed by the OIDC handler — drift here is a runtime SSO incident.
/// </summary>
public class EntraIdFlavorTests
{
    private static JsonDocument Json(string raw) => JsonDocument.Parse(raw);

    public class Identity
    {
        [Fact]
        public void Key_is_the_canonical_EntraId_constant()
        {
            var flavor = new EntraIdFlavor();
            Assert.Equal(LoginProviderFlavor.EntraId, flavor.Key);
            Assert.Equal("EntraId", flavor.Key);
        }

        [Fact]
        public void Display_name_and_icon_are_stable()
        {
            var flavor = new EntraIdFlavor();
            Assert.Equal("Microsoft Entra ID", flavor.DisplayName);
            Assert.Equal("microsoft", flavor.DefaultIconName);
        }

        [Fact]
        public void Default_scopes_cover_openid_profile_email()
        {
            var flavor = new EntraIdFlavor();
            Assert.Equal(new[] { "openid", "profile", "email" }, flavor.DefaultScopes);
        }

        [Fact]
        public void Enterprise_defaults_store_raw_claims()
        {
            // Entra is an enterprise flavor — claim debugging is mission-critical,
            // so raw-claim capture defaults on.
            Assert.True(new EntraIdFlavor().DefaultStoreRawClaims);
        }

        [Fact]
        public void Default_user_update_script_is_non_empty_arrow_function()
        {
            var script = new EntraIdFlavor().DefaultUserUpdateScript;
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
        public void Connection_field_is_TenantId_plus_shared_advanced()
        {
            var schema = new EntraIdFlavor().ConfigSchema;

            var connection = schema.Where(f => f.Section == FlavorConfigSections.Connection).ToList();
            Assert.Single(connection);
            Assert.Equal("TenantId", connection[0].Key);

            var advanced = schema.Where(f => f.Section == FlavorConfigSections.Advanced).Select(f => f.Key).ToList();
            Assert.Contains("UsePkce", advanced);
            Assert.Contains("GetClaimsFromUserInfoEndpoint", advanced);
            Assert.Contains("SaveTokens", advanced);
            Assert.Contains("Prompt", advanced);
        }

        [Fact]
        public void TenantId_field_is_a_required_string_with_help_text()
        {
            var field = new EntraIdFlavor().ConfigSchema.Single(f => f.Key == "TenantId");
            Assert.Equal(FlavorConfigFieldType.String, field.Type);
            Assert.True(field.Required);
            Assert.Equal("Tenant ID", field.Label);
            Assert.False(string.IsNullOrWhiteSpace(field.HelpText));
            Assert.False(string.IsNullOrWhiteSpace(field.Placeholder));
        }
    }

    public class DeriveEndpoints
    {
        [Fact]
        public void Builds_v2_authority_and_metadata_uri_from_tenant_id()
        {
            var flavor = new EntraIdFlavor();
            using var doc = Json("""{"TenantId":"00000000-0000-0000-0000-000000000001"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal(
                "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0",
                endpoints.Authority);
            Assert.Equal(
                "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0/.well-known/openid-configuration",
                endpoints.MetadataUri);
        }

        [Fact]
        public void Accepts_the_common_multitenant_alias()
        {
            var flavor = new EntraIdFlavor();
            using var doc = Json("""{"TenantId":"common"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Equal("https://login.microsoftonline.com/common/v2.0", endpoints.Authority);
            Assert.Equal(
                "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
                endpoints.MetadataUri);
        }

        [Fact]
        public void Leaves_explicit_endpoints_unset_so_OIDC_handler_uses_discovery()
        {
            var flavor = new EntraIdFlavor();
            using var doc = Json("""{"TenantId":"contoso.onmicrosoft.com"}""");

            var endpoints = flavor.DeriveEndpoints(doc);

            Assert.Null(endpoints.AuthorizationEndpoint);
            Assert.Null(endpoints.TokenEndpoint);
            Assert.Null(endpoints.UserInfoEndpoint);
            Assert.Null(endpoints.EndSessionEndpoint);
        }

        [Fact]
        public void Throws_when_flavor_data_is_null()
        {
            var ex = Assert.Throws<ArgumentException>(() => new EntraIdFlavor().DeriveEndpoints(null));
            Assert.Equal("flavorData", ex.ParamName);
        }

        [Fact]
        public void Throws_when_tenant_id_property_is_missing()
        {
            using var doc = Json("""{"OtherProp":"value"}""");
            var ex = Assert.Throws<ArgumentException>(() => new EntraIdFlavor().DeriveEndpoints(doc));
            Assert.Equal("flavorData", ex.ParamName);
        }

        [Fact]
        public void Throws_when_tenant_id_is_empty_string()
        {
            using var doc = Json("""{"TenantId":""}""");
            Assert.Throws<ArgumentException>(() => new EntraIdFlavor().DeriveEndpoints(doc));
        }

        [Fact]
        public void Throws_when_tenant_id_is_whitespace_only()
        {
            using var doc = Json("""{"TenantId":"   "}""");
            Assert.Throws<ArgumentException>(() => new EntraIdFlavor().DeriveEndpoints(doc));
        }

        [Fact]
        public void Throws_when_tenant_id_is_not_a_string()
        {
            using var doc = Json("""{"TenantId":12345}""");
            Assert.Throws<ArgumentException>(() => new EntraIdFlavor().DeriveEndpoints(doc));
        }
    }
}
