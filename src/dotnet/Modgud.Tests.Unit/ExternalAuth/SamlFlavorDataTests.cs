using System.Text.Json;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// Pins the SAML <see cref="SamlFlavorData"/> shape and JSON contract. This is
/// what gets persisted in <c>LoginProvider.FlavorData</c>; the shape is read by
/// admin UI, by the dynamic scheme manager at login time, and by the metadata
/// refresh job — any drift in the round-trip is a multi-consumer incident.
/// </summary>
public class SamlFlavorDataTests
{
    private static JsonDocument Json(string raw) => JsonDocument.Parse(raw);

    public class SignatureFloor
    {
        [Fact]
        public void Both_signing_toggles_off_is_rejected()
        {
            // Audit L2: a config that requires neither assertion- nor
            // response-signing would accept entirely unsigned SAML.
            var data = SamlFlavorData.FromJson(
                JsonDocument.Parse("""{"wantAssertionsSigned":false,"wantResponseSigned":false}"""));

            var error = data.ValidateSignatureFloor();

            Assert.NotNull(error);
            Assert.Equal("LoginProvider.SamlNoSignatureRequired", error!.Value.Code);
        }

        [Fact]
        public void Assertion_signing_on_passes()
        {
            // The default (assertion signing on, response signing off — the
            // EntraID/ADFS shape) must remain valid.
            Assert.Null(SamlFlavorData.FromJson(null).ValidateSignatureFloor());
        }

        [Fact]
        public void Response_signing_on_passes()
        {
            var data = SamlFlavorData.FromJson(
                JsonDocument.Parse("""{"wantAssertionsSigned":false,"wantResponseSigned":true}"""));
            Assert.Null(data.ValidateSignatureFloor());
        }
    }

    public class Defaults
    {
        [Fact]
        public void Null_input_yields_defaults()
        {
            var data = SamlFlavorData.FromJson(null);

            Assert.Null(data.MetadataUrl);
            Assert.Null(data.MetadataXml);
            Assert.Null(data.EntityId);
            Assert.Empty(data.SigningCertificates);
            Assert.Equal(SamlNameIdFormats.EmailAddress, data.NameIdFormat);
            Assert.True(data.WantAssertionsSigned);
            Assert.False(data.WantResponseSigned);
            Assert.False(data.WantAssertionsEncrypted);
            Assert.True(data.SignAuthnRequest);
            Assert.Empty(data.AttributeMap);
            Assert.Empty(data.AmrMapping);
            Assert.Equal(SamlFlavorData.DefaultMetadataRefreshIntervalSeconds, data.MetadataRefreshIntervalSeconds);
        }

        [Fact]
        public void Empty_object_yields_defaults()
        {
            var data = SamlFlavorData.FromJson(Json("{}"));

            Assert.True(data.WantAssertionsSigned);
            Assert.False(data.WantResponseSigned);
            Assert.True(data.SignAuthnRequest);
            Assert.False(data.WantAssertionsEncrypted);
            Assert.Equal(86_400, data.MetadataRefreshIntervalSeconds);
        }

        [Fact]
        public void Default_refresh_interval_is_24h()
        {
            Assert.Equal(86_400, SamlFlavorData.DefaultMetadataRefreshIntervalSeconds);
        }
    }

    public class Parse
    {
        [Fact]
        public void Reads_metadata_url()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "metadataUrl": "https://login.microsoftonline.com/tenant/federationmetadata/2007-06/federationmetadata.xml"
                }
                """));

            Assert.Equal(
                "https://login.microsoftonline.com/tenant/federationmetadata/2007-06/federationmetadata.xml",
                data.MetadataUrl);
        }

        [Fact]
        public void Reads_signing_certificates_array()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "signingCertificates": ["MIIcert1==", "MIIcert2=="]
                }
                """));

            Assert.Equal(["MIIcert1==", "MIIcert2=="], data.SigningCertificates);
        }

        [Fact]
        public void Reads_attribute_map_with_array_values()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "attributeMap": {
                    "email": [
                      "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                      "email"
                    ],
                    "name": ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"]
                  }
                }
                """));

            Assert.Equal(2, data.AttributeMap.Count);
            Assert.Equal(
                ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "email"],
                data.AttributeMap["email"]);
            Assert.Equal(
                ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"],
                data.AttributeMap["name"]);
        }

        [Fact]
        public void Reads_attribute_map_with_scalar_string_value_as_single_element_list()
        {
            // Tolerate the shorthand where a single-attribute mapping is stored
            // as a bare string rather than a one-element array.
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "attributeMap": {
                    "email": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
                  }
                }
                """));

            Assert.Equal(["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"], data.AttributeMap["email"]);
        }

        [Fact]
        public void Reads_amr_mapping()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "amrMapping": {
                    "urn:oasis:names:tc:SAML:2.0:ac:classes:Password": ["pwd"],
                    "urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor": ["mfa"]
                  }
                }
                """));

            Assert.Equal(["pwd"], data.AmrMapping["urn:oasis:names:tc:SAML:2.0:ac:classes:Password"]);
            Assert.Equal(["mfa"], data.AmrMapping["urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"]);
        }

        [Fact]
        public void Reads_explicit_refresh_interval()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "metadataRefreshIntervalSeconds": 3600
                }
                """));

            Assert.Equal(3600, data.MetadataRefreshIntervalSeconds);
        }

        [Fact]
        public void Reads_explicit_security_toggles()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "wantAssertionsSigned": false,
                  "wantResponseSigned": false,
                  "wantAssertionsEncrypted": true,
                  "signAuthnRequest": false
                }
                """));

            Assert.False(data.WantAssertionsSigned);
            Assert.False(data.WantResponseSigned);
            Assert.True(data.WantAssertionsEncrypted);
            Assert.False(data.SignAuthnRequest);
        }

        [Fact]
        public void Tolerates_unknown_fields()
        {
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "metadataUrl": "https://example.com/metadata",
                  "futureUnknownField": "ignored",
                  "anotherOne": { "nested": [1,2,3] }
                }
                """));

            Assert.Equal("https://example.com/metadata", data.MetadataUrl);
        }

        [Fact]
        public void Non_object_root_yields_defaults()
        {
            // Defensive — should not happen in production, but if FlavorData
            // ever gets stored as an array or scalar, fall back to defaults
            // instead of throwing.
            var data = SamlFlavorData.FromJson(Json("[]"));
            Assert.True(data.WantAssertionsSigned);
            Assert.Equal(86_400, data.MetadataRefreshIntervalSeconds);
        }

        [Fact]
        public void When_both_camel_and_pascal_present_PascalCase_wins()
        {
            // The frontend's FlavorConnectionPanel writes admin input keyed by
            // FlavorConfigField.Key (PascalCase) — e.g. `MetadataUrl`. The
            // backend's ToJson canonical form is camelCase (`metadataUrl`).
            // After an admin Update where the modal spread the prior camelCase
            // FlavorData into form.FlavorData and then wrote a new value under
            // the PascalCase key, the POST body carries BOTH forms with
            // different values. The PascalCase one IS the admin's intent;
            // the camelCase one is the stale pre-edit value the modal
            // started from. Prefer PascalCase so the admin's edit is honored.
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "metadataUrl": "https://stale.invalid/metadata",
                  "MetadataUrl": "https://login.microsoftonline.com/tenant/metadata.xml"
                }
                """));

            Assert.Equal("https://login.microsoftonline.com/tenant/metadata.xml", data.MetadataUrl);
        }

        [Fact]
        public void When_PascalCase_is_null_camel_wins()
        {
            // Inverse case: if PascalCase exists but carries null/undefined
            // (which happens if a forwards-compat client cleared the field
            // explicitly), the camelCase canonical value should win.
            var data = SamlFlavorData.FromJson(Json("""
                {
                  "metadataUrl": "https://canonical.example/metadata.xml",
                  "MetadataUrl": null
                }
                """));

            Assert.Equal("https://canonical.example/metadata.xml", data.MetadataUrl);
        }
    }

    public class RoundTrip
    {
        [Fact]
        public void Full_record_round_trips_through_json()
        {
            var original = new SamlFlavorData
            {
                MetadataUrl = "https://idp.example.com/metadata",
                MetadataXml = null,
                EntityId = "https://idp.example.com/entity",
                SigningCertificates = ["MIIabc==", "MIIxyz=="],
                NameIdFormat = SamlNameIdFormats.Persistent,
                WantAssertionsSigned = true,
                WantResponseSigned = false,
                WantAssertionsEncrypted = true,
                SignAuthnRequest = false,
                AttributeMap = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["email"] = ["urn:oid:0.9.2342.19200300.100.1.3", "email"],
                    ["groups"] = ["http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"],
                },
                AmrMapping = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"] = ["mfa"],
                },
                MetadataRefreshIntervalSeconds = 21_600, // 6h
            };

            using var doc = original.ToJson();
            var restored = SamlFlavorData.FromJson(doc);

            Assert.Equal(original.MetadataUrl, restored.MetadataUrl);
            Assert.Equal(original.EntityId, restored.EntityId);
            Assert.Equal(original.SigningCertificates, restored.SigningCertificates);
            Assert.Equal(original.NameIdFormat, restored.NameIdFormat);
            Assert.Equal(original.WantAssertionsSigned, restored.WantAssertionsSigned);
            Assert.Equal(original.WantResponseSigned, restored.WantResponseSigned);
            Assert.Equal(original.WantAssertionsEncrypted, restored.WantAssertionsEncrypted);
            Assert.Equal(original.SignAuthnRequest, restored.SignAuthnRequest);
            Assert.Equal(original.MetadataRefreshIntervalSeconds, restored.MetadataRefreshIntervalSeconds);

            Assert.Equal(original.AttributeMap.Count, restored.AttributeMap.Count);
            foreach (var (k, v) in original.AttributeMap)
                Assert.Equal(v, restored.AttributeMap[k]);

            Assert.Equal(original.AmrMapping.Count, restored.AmrMapping.Count);
            foreach (var (k, v) in original.AmrMapping)
                Assert.Equal(v, restored.AmrMapping[k]);
        }

        [Fact]
        public void Defaults_round_trip_to_defaults()
        {
            var original = new SamlFlavorData();
            using var doc = original.ToJson();
            var restored = SamlFlavorData.FromJson(doc);

            Assert.Equal(original.NameIdFormat, restored.NameIdFormat);
            Assert.Equal(original.WantAssertionsSigned, restored.WantAssertionsSigned);
            Assert.Equal(original.WantResponseSigned, restored.WantResponseSigned);
            Assert.Equal(original.WantAssertionsEncrypted, restored.WantAssertionsEncrypted);
            Assert.Equal(original.SignAuthnRequest, restored.SignAuthnRequest);
            Assert.Equal(original.MetadataRefreshIntervalSeconds, restored.MetadataRefreshIntervalSeconds);
        }
    }

    public class NameIdFormats
    {
        [Theory]
        [InlineData("urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", "EmailAddress")]
        [InlineData("urn:oasis:names:tc:SAML:2.0:nameid-format:persistent", "Persistent")]
        [InlineData("urn:oasis:names:tc:SAML:2.0:nameid-format:transient", "Transient")]
        [InlineData("urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified", "Unspecified")]
        public void Constants_match_well_known_urns(string urn, string name)
        {
            // Pinned because IdPs validate the exact URN on the wire — a typo
            // here is an immediate auth-broken incident.
            var actual = name switch
            {
                "EmailAddress" => SamlNameIdFormats.EmailAddress,
                "Persistent" => SamlNameIdFormats.Persistent,
                "Transient" => SamlNameIdFormats.Transient,
                "Unspecified" => SamlNameIdFormats.Unspecified,
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
            Assert.Equal(urn, actual);
        }
    }
}
