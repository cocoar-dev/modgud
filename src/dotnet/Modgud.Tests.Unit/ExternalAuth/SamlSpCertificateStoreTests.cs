using Microsoft.AspNetCore.DataProtection;
using Modgud.Authentication.Domain.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// Pure-construction tests for <see cref="SamlSpCertificateStore"/> +
/// <see cref="SamlSpCertificateDocument"/> shape. The service-level
/// lifecycle (lazy-generate-on-first-read, rotate, retire) needs a real
/// Marten session and gets coverage in the integration test project once
/// the SAML protocol slice lands.
/// </summary>
public class SamlSpCertificateStoreTests
{
    private static SamlSpCertificateStore NewStore() =>
        new(new EphemeralDataProtectionProvider());

    public class StoreRoundTrip
    {
        [Fact]
        public void Encrypt_then_decrypt_returns_original_bytes()
        {
            var store = NewStore();
            var original = new byte[] { 1, 2, 3, 4, 5, 0, 255, 200 };

            var encrypted = store.Encrypt(original);
            var decrypted = store.Decrypt(encrypted);

            Assert.Equal(original, decrypted);
        }

        [Fact]
        public void Encrypted_bytes_differ_from_plaintext()
        {
            var store = NewStore();
            var original = new byte[] { 1, 2, 3, 4, 5 };

            var encrypted = store.Encrypt(original);

            Assert.NotEqual(original, encrypted);
            Assert.True(encrypted.Length > original.Length, "expected ciphertext + auth tag to be larger than plaintext");
        }

        [Fact]
        public void TryDecrypt_returns_null_for_null_input()
        {
            var store = NewStore();
            Assert.Null(store.TryDecrypt(null));
        }

        [Fact]
        public void TryDecrypt_returns_null_for_empty_input()
        {
            var store = NewStore();
            Assert.Null(store.TryDecrypt([]));
        }

        [Fact]
        public void TryDecrypt_returns_null_for_garbage_input()
        {
            var store = NewStore();
            Assert.Null(store.TryDecrypt([0xFF, 0xFF, 0xFF, 0xFF]));
        }

        [Fact]
        public void Cross_store_decrypt_fails_due_to_different_keyring()
        {
            // Two ephemeral providers = two independent keyrings; bytes
            // encrypted by store A must not be decryptable by store B.
            var storeA = NewStore();
            var storeB = NewStore();

            var encrypted = storeA.Encrypt([1, 2, 3]);

            Assert.Null(storeB.TryDecrypt(encrypted));
        }
    }

    public class DocumentShape
    {
        [Fact]
        public void Singleton_id_is_stable_across_test_runs()
        {
            // The Id is part of the data contract — changing it would orphan
            // every existing SP cert in production tenant DBs.
            Assert.Equal(
                Guid.Parse("00000000-0000-0000-0000-00000000A4ED"),
                SamlSpCertificateDocument.SingletonId);
        }

        [Fact]
        public void New_document_defaults_to_singleton_id()
        {
            var doc = new SamlSpCertificateDocument();
            Assert.Equal(SamlSpCertificateDocument.SingletonId, doc.Id);
        }

        [Fact]
        public void New_document_has_no_active_cert_bytes()
        {
            var doc = new SamlSpCertificateDocument();
            Assert.Empty(doc.ActiveCertPfxEncrypted);
        }

        [Fact]
        public void New_document_has_no_previous_cert()
        {
            var doc = new SamlSpCertificateDocument();
            Assert.Null(doc.PreviousCertPfxEncrypted);
            Assert.Null(doc.PreviousCertThumbprint);
            Assert.Null(doc.PreviousCertNotAfter);
            Assert.Null(doc.PreviousRetiresAt);
        }
    }

    public class ServiceDefaults
    {
        [Fact]
        public void Rotation_overlap_is_30_days()
        {
            // Stable contract: SP-metadata advertise-both window. IdPs
            // refresh metadata at ~24h; 30d is two orders of magnitude
            // safety margin.
            Assert.Equal(TimeSpan.FromDays(30), SamlSpCertificateService.RotationOverlap);
        }

        [Fact]
        public void Default_validity_is_two_years()
        {
            Assert.Equal(TimeSpan.FromDays(365 * 2), SamlSpCertificateService.DefaultValidity);
        }

        [Fact]
        public void Subject_cn_prefix_is_stable()
        {
            Assert.Equal("modgud-sp", SamlSpCertificateService.SubjectCnPrefix);
        }
    }
}
