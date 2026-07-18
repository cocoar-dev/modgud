using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modgud.Api;
using Modgud.Api.Startup;
using Modgud.Infrastructure.OpenIddict;
using OpenIddict.Server;
using Xunit;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Issue #125 — rotation overlap for the OpenIddict encryption certificate.
/// Mirrors <c>PreviousSigningCertificatePaths</c>, which had no equivalent
/// wiring test before this (<c>PkceRequirementPinTests</c> only exercises
/// <c>DevelopmentMode</c>, which skips the cert-loading branch entirely).
/// This pins that <c>PreviousEncryptionCertificatePaths</c> actually reaches
/// <c>OpenIddictServerOptions.EncryptionCredentials</c> — so a token
/// JWE-encrypted under the outgoing cert still decrypts during the overlap
/// window — instead of only existing as an unused settings property.
/// </summary>
public class EncryptionCertificateOverlapTests : IDisposable
{
    private readonly string _dir;

    public EncryptionCertificateOverlapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "modgud-enc-overlap-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Previous_encryption_certificate_is_loaded_alongside_the_active_one_and_stays_decryption_only()
    {
        var signingPath = Path.Combine(_dir, "signing.pfx");
        var activePath = Path.Combine(_dir, "encryption-active.pfx");
        var previousPath = Path.Combine(_dir, "encryption-previous.pfx");

        CertificateBootstrap.GenerateSelfSignedPfx(signingPath, "CN=Test Signing",
            X509KeyUsageFlags.DigitalSignature, validYears: 2, keySize: 2048);

        // OpenIddict re-sorts EncryptionCredentials by X.509 expiration and
        // treats the furthest-expiring cert as active for new tokens (see
        // OpenIddictServerConfiguration.Compare) — it does NOT simply use
        // insertion order. Give the "active" cert a longer validity window
        // than the "previous" one so the test reflects a real rotation
        // (fresh cert, full validity) rather than relying on sub-millisecond
        // clock ordering between two otherwise-identical certs.
        CertificateBootstrap.GenerateSelfSignedPfx(activePath, "CN=Test Encryption Active",
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, validYears: 5, keySize: 2048);
        CertificateBootstrap.GenerateSelfSignedPfx(previousPath, "CN=Test Encryption Previous",
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, validYears: 1, keySize: 2048);

        var settings = new OpenIddictSettings
        {
            DevelopmentMode = false,
            SigningCertificatePath = signingPath,
            EncryptionCertificatePath = activePath,
            PreviousEncryptionCertificatePaths = [previousPath],
        };

        var options = BuildServerOptions(settings);

        // Both certs must be present so an artifact JWE-encrypted under
        // either one still decrypts during the overlap window.
        Assert.Equal(2, options.EncryptionCredentials.Count);

        using var activeCert = X509CertificateLoader.LoadPkcs12FromFile(activePath, password: null);
        using var previousCert = X509CertificateLoader.LoadPkcs12FromFile(previousPath, password: null);

        var thumbprints = options.EncryptionCredentials
            .Select(credentials => ((X509SecurityKey)credentials.Key).Certificate.Thumbprint)
            .ToList();
        Assert.Contains(activeCert.Thumbprint, thumbprints);
        Assert.Contains(previousCert.Thumbprint, thumbprints);

        // The furthest-expiring (active) cert is the one OpenIddict actually
        // uses to encrypt new tokens.
        var activeCredential = ((X509SecurityKey)options.EncryptionCredentials[0].Key).Certificate;
        Assert.Equal(activeCert.Thumbprint, activeCredential.Thumbprint);
    }

    [Fact]
    public void Unset_previous_encryption_paths_load_only_the_active_certificate()
    {
        var signingPath = Path.Combine(_dir, "signing.pfx");
        var activePath = Path.Combine(_dir, "encryption-active.pfx");

        CertificateBootstrap.GenerateSelfSignedPfx(signingPath, "CN=Test Signing",
            X509KeyUsageFlags.DigitalSignature, validYears: 2, keySize: 2048);
        CertificateBootstrap.GenerateSelfSignedPfx(activePath, "CN=Test Encryption Active",
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, validYears: 2, keySize: 2048);

        var settings = new OpenIddictSettings
        {
            DevelopmentMode = false,
            SigningCertificatePath = signingPath,
            EncryptionCertificatePath = activePath,
        };

        var options = BuildServerOptions(settings);

        Assert.Single(options.EncryptionCredentials);
    }

    private static OpenIddictServerOptions BuildServerOptions(OpenIddictSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new TestEnvironment());
        services.AddOpenIddictWithMarten(settings);

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>();
        return monitor.CurrentValue;
    }

    private sealed class TestEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
    }
}
