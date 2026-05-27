using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// Pins identity (Key + DisplayName) and default-application behaviour for the
/// three SAML flavors. Identity drift breaks stored Flavor strings on
/// LoginProvider records; default drift changes the first-time-add UX silently.
/// </summary>
public class SamlFlavorTests
{
    public class GenericFlavor
    {
        [Fact]
        public void Identity_is_canonical()
        {
            var flavor = new GenericSamlFlavor();
            Assert.Equal(LoginProviderFlavor.GenericSaml, flavor.Key);
            Assert.Equal("GenericSaml", flavor.Key);
            Assert.Equal("Generic SAML 2.0", flavor.DisplayName);
            Assert.True(flavor.DefaultStoreRawClaims);
        }

        [Fact]
        public void ApplyDefaults_on_null_returns_record_with_no_vendor_specific_fields()
        {
            var flavor = new GenericSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Empty(data.AttributeMap);
            Assert.Empty(data.AmrMapping);
            Assert.Equal(SamlNameIdFormats.EmailAddress, data.NameIdFormat);
        }

        [Fact]
        public void ApplyDefaults_preserves_admin_provided_data()
        {
            var flavor = new GenericSamlFlavor();
            var input = new SamlFlavorData
            {
                MetadataUrl = "https://idp.example.com/metadata",
                NameIdFormat = SamlNameIdFormats.Persistent,
            };

            var data = flavor.ApplyDefaults(input);

            Assert.Equal("https://idp.example.com/metadata", data.MetadataUrl);
            Assert.Equal(SamlNameIdFormats.Persistent, data.NameIdFormat);
        }

        [Fact]
        public void Config_schema_lists_metadata_url_and_xml()
        {
            var flavor = new GenericSamlFlavor();
            var keys = flavor.ConfigSchema.Select(f => f.Key).ToArray();
            Assert.Contains("MetadataUrl", keys);
            Assert.Contains("MetadataXml", keys);
        }
    }

    public class EntraIdFlavor
    {
        [Fact]
        public void Identity_is_canonical()
        {
            var flavor = new EntraIdSamlFlavor();
            Assert.Equal(LoginProviderFlavor.EntraIdSaml, flavor.Key);
            Assert.Equal("EntraIdSaml", flavor.Key);
            Assert.Equal("Microsoft Entra ID (SAML)", flavor.DisplayName);
            Assert.Equal("microsoft", flavor.DefaultIconName);
        }

        [Fact]
        public void ApplyDefaults_seeds_microsoft_claim_uris_for_email_and_name()
        {
            var flavor = new EntraIdSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                data.AttributeMap["email"]);
            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
                data.AttributeMap["given_name"]);
            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
                data.AttributeMap["family_name"]);
        }

        [Fact]
        public void ApplyDefaults_seeds_microsoft_groups_claim_uri()
        {
            var flavor = new EntraIdSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Contains("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups",
                data.AttributeMap["groups"]);
        }

        [Fact]
        public void ApplyDefaults_seeds_mfa_amr_mapping()
        {
            var flavor = new EntraIdSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Contains("mfa",
                data.AmrMapping["urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"]);
            Assert.Contains("mfa",
                data.AmrMapping["http://schemas.microsoft.com/claims/multipleauthn"]);
        }

        [Fact]
        public void ApplyDefaults_does_not_overwrite_existing_attribute_map()
        {
            var flavor = new EntraIdSamlFlavor();
            var input = new SamlFlavorData
            {
                AttributeMap = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["email"] = ["custom-email-claim"],
                },
            };

            var data = flavor.ApplyDefaults(input);

            // Admin-provided map wins as a whole; flavor doesn't merge.
            Assert.Single(data.AttributeMap);
            Assert.Equal(["custom-email-claim"], data.AttributeMap["email"]);
        }

        [Fact]
        public void Config_schema_marks_metadata_url_required()
        {
            var flavor = new EntraIdSamlFlavor();
            var metadataField = flavor.ConfigSchema.Single(f => f.Key == "MetadataUrl");
            Assert.True(metadataField.Required);
        }
    }

    public class AdfsFlavor
    {
        [Fact]
        public void Identity_is_canonical()
        {
            var flavor = new AdfsSamlFlavor();
            Assert.Equal(LoginProviderFlavor.AdfsSaml, flavor.Key);
            Assert.Equal("AdfsSaml", flavor.Key);
            Assert.Equal("Active Directory Federation Services (SAML)", flavor.DisplayName);
        }

        [Fact]
        public void ApplyDefaults_seeds_ad_claim_uris()
        {
            var flavor = new AdfsSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                data.AttributeMap["email"]);
            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn",
                data.AttributeMap["upn"]);
            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/windowsaccountname",
                data.AttributeMap["windowsaccountname"]);
        }

        [Fact]
        public void ApplyDefaults_seeds_windows_authentication_amr()
        {
            var flavor = new AdfsSamlFlavor();
            var data = flavor.ApplyDefaults(null);

            Assert.Contains("pwd",
                data.AmrMapping["urn:federation:authentication:windows"]);
        }

        [Fact]
        public void Config_schema_does_not_require_metadata_url()
        {
            // ADFS often behind a customer firewall — MetadataXml paste is a
            // legitimate alternative.
            var flavor = new AdfsSamlFlavor();
            var metadataField = flavor.ConfigSchema.Single(f => f.Key == "MetadataUrl");
            Assert.False(metadataField.Required);
        }
    }

    public class Registry
    {
        private static SamlFlavorRegistry Build() =>
            new(new ISamlFlavor[]
            {
                new GenericSamlFlavor(),
                new EntraIdSamlFlavor(),
                new AdfsSamlFlavor(),
            });

        [Fact]
        public void All_returns_every_registered_flavor()
        {
            var registry = Build();
            Assert.Equal(3, registry.All.Count());
        }

        [Theory]
        [InlineData(LoginProviderFlavor.GenericSaml, typeof(GenericSamlFlavor))]
        [InlineData(LoginProviderFlavor.EntraIdSaml, typeof(EntraIdSamlFlavor))]
        [InlineData(LoginProviderFlavor.AdfsSaml, typeof(AdfsSamlFlavor))]
        public void Get_resolves_each_known_key(string key, Type expectedType)
        {
            var registry = Build();
            Assert.IsType(expectedType, registry.Get(key));
        }

        [Fact]
        public void Get_is_case_insensitive()
        {
            var registry = Build();
            Assert.IsType<EntraIdSamlFlavor>(registry.Get("entraidsaml"));
        }

        [Fact]
        public void Get_throws_descriptively_for_unknown_key()
        {
            var registry = Build();
            var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("OktaSaml"));
            Assert.Contains("OktaSaml", ex.Message);
            Assert.Contains("EntraIdSaml", ex.Message); // known keys listed in message
        }

        [Fact]
        public void TryGet_returns_false_for_unknown_key()
        {
            var registry = Build();
            Assert.False(registry.TryGet("OktaSaml", out _));
        }

        [Fact]
        public void Saml_and_oidc_entra_keys_are_distinct()
        {
            // Important: the OIDC EntraID flavor is keyed "EntraId", the SAML
            // one is "EntraIdSaml". They coexist in the LoginProviderFlavor
            // catalog. If these ever collide, a SAML provider could be
            // resolved as an OIDC one (or vice versa) — silent runtime auth
            // misbehaviour.
            Assert.NotEqual(LoginProviderFlavor.EntraId, LoginProviderFlavor.EntraIdSaml);
        }
    }
}
