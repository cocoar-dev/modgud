using System.Security.Cryptography.X509Certificates;
using Modgud.Api;
using Modgud.Api.Startup;

namespace Modgud.Tests.Unit.Startup;

/// <summary>
/// Stage 0 of the cold-start ladder. Closes the long-standing coverage gap on
/// the OpenIddict signing/encryption certificate auto-generation path — code
/// that previously only ever ran on a real, non-Development boot. Pins that a
/// cold boot produces a loadable cert with the key-usage OpenIddict requires,
/// and that a second boot reuses it (regenerating would invalidate every token
/// issued under the first key).
/// </summary>
public class CertificateBootstrapTests : IDisposable
{
    private readonly string _dir;

    public CertificateBootstrapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "modgud-cert-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Signing_cert_is_generated_loadable_and_carries_DigitalSignature_usage()
    {
        var path = Path.Combine(_dir, "signing.pfx");
        var settings = new OpenIddictSettings { SigningCertificatePath = path };

        CertificateBootstrap.EnsureSigningCertificateExists(settings);

        Assert.True(File.Exists(path));

        using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        Assert.True(cert.HasPrivateKey);
        var keyUsage = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        // OpenIddict's AddSigningCertificate rejects certs without DigitalSignature.
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
    }

    [Fact]
    public void Encryption_cert_is_generated_and_carries_KeyEncipherment_usage()
    {
        var path = Path.Combine(_dir, "encryption.pfx");
        var settings = new OpenIddictSettings { EncryptionCertificatePath = path };

        CertificateBootstrap.EnsureEncryptionCertificateExists(settings);

        Assert.True(File.Exists(path));

        using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        var keyUsage = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        // OpenIddict's AddEncryptionCertificate wraps a content-encryption key.
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));
    }

    [Fact]
    public void A_second_boot_reuses_the_existing_cert_unchanged()
    {
        var path = Path.Combine(_dir, "signing.pfx");
        var settings = new OpenIddictSettings { SigningCertificatePath = path };

        CertificateBootstrap.EnsureSigningCertificateExists(settings);
        var firstBytes = File.ReadAllBytes(path);

        // A restart must not regenerate — that would invalidate every token
        // issued under the first key.
        CertificateBootstrap.EnsureSigningCertificateExists(settings);
        var secondBytes = File.ReadAllBytes(path);

        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public void Unset_path_resolves_to_the_default_keys_directory()
    {
        // Leaving the path unset resolves to the data/keys/<purpose>.pfx default
        // and writes the resolved absolute path back onto the settings — so the
        // later OpenIddict AddSigningCertificate(path) call has a concrete file.
        var settings = new OpenIddictSettings { SigningCertificatePath = null };
        // FK-12 — the default lives under the working directory: a certificate already
        // there (a developer's own data/keys) is reused by the bootstrap and must survive.
        var defaultPath = Modgud.Api.Helper.PathHelper.GetFullPath("data/keys/signing.pfx");
        var existedBefore = File.Exists(defaultPath);

        CertificateBootstrap.EnsureSigningCertificateExists(settings);

        Assert.False(string.IsNullOrWhiteSpace(settings.SigningCertificatePath));
        Assert.Contains("signing.pfx", settings.SigningCertificatePath!);
        Assert.True(File.Exists(settings.SigningCertificatePath));

        // Clean up only what this test generated.
        if (!existedBefore)
            try { File.Delete(settings.SigningCertificatePath!); } catch { /* best-effort */ }
    }

    /// <summary>FK-07 — a second node that lost the race on an empty shared volume
    /// must not overwrite the certificate the first one wrote (tokens are already
    /// signed with it).</summary>
    [Fact]
    public void Generating_onto_an_existing_file_keeps_that_file()
    {
        var path = Path.Combine(_dir, "taken.pfx");
        File.WriteAllBytes(path, [1, 2, 3]);

        CertificateBootstrap.GenerateSelfSignedPfx(path, "CN=Late", X509KeyUsageFlags.DigitalSignature, validYears: 1, keySize: 2048);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir)); // no temp file left behind
    }

    /// <summary>FK-07 — the keys directory holds private keys: owner only.</summary>
    [Fact]
    public void The_keys_directory_is_created_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "fresh", "keys", "signing.pfx");

        CertificateBootstrap.EnsureSigningCertificateExists(new OpenIddictSettings { SigningCertificatePath = path });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
