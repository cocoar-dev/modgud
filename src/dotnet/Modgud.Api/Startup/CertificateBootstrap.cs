using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Modgud.Api.Helper;
using Serilog;

namespace Modgud.Api.Startup;

/// <summary>
/// Cold-boot certificate bootstrap for the OpenIddict signing + encryption
/// keys (CERT-01 / OAUTH-05). Extracted from <c>Program.cs</c> so the
/// auto-generation path — previously only ever exercised on a real non-dev
/// boot — is unit-testable in isolation.
///
/// <para>Convention: a passwordless PFX protected by file-system permissions
/// (0600 on Linux), per the cocoar-secrets CLI tool's recommendation. When the
/// configured path (or the default) doesn't exist on disk, a self-signed cert
/// is generated there — it survives container restarts when the directory is
/// mounted as a volume, so tokens stay valid. Callers skip this entirely in
/// DevelopmentMode, where OpenIddict uses ephemeral keys.</para>
/// </summary>
public static class CertificateBootstrap
{
    /// <summary>
    /// Resolve <c>SigningCertificatePath</c> (or its default
    /// <c>data/keys/signing.pfx</c>) and ensure the file exists, generating a
    /// self-signed cert with the <c>DigitalSignature</c> key usage OpenIddict's
    /// <c>AddSigningCertificate</c> requires.
    /// </summary>
    public static void EnsureSigningCertificateExists(OpenIddictSettings settings)
        => EnsureCertificateExists(
            () => settings.SigningCertificatePath,
            path => settings.SigningCertificatePath = path,
            defaultRelativePath: "data/keys/signing.pfx",
            subject: "CN=Modgud Signing",
            purpose: "signing",
            // OpenIddict's AddSigningCertificate rejects certs that don't
            // declare DigitalSignature in their X509KeyUsage extension.
            keyUsage: X509KeyUsageFlags.DigitalSignature);

    /// <summary>
    /// Resolve <c>EncryptionCertificatePath</c> (or its default
    /// <c>data/keys/encryption.pfx</c>) and ensure the file exists. Same
    /// auto-generation behaviour as the signing cert. Falls back to the
    /// signing cert at use-site when the path stays unresolved (legacy
    /// behaviour) — kept so an operator who deliberately leaves
    /// EncryptionCertificatePath unset still gets a working server.
    /// </summary>
    public static void EnsureEncryptionCertificateExists(OpenIddictSettings settings)
        => EnsureCertificateExists(
            () => settings.EncryptionCertificatePath,
            path => settings.EncryptionCertificatePath = path,
            defaultRelativePath: "data/keys/encryption.pfx",
            subject: "CN=Modgud Encryption",
            purpose: "encryption",
            // OpenIddict's AddEncryptionCertificate wants a cert that can
            // wrap a content-encryption key — KeyEncipherment covers RSA-OAEP
            // wrapping which is what OpenIddict uses when token encryption
            // is enabled.
            keyUsage: X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment);

    /// <summary>
    /// Shared certificate-bootstrap path. Path is resolved via
    /// <see cref="PathHelper.GetFullPath"/> so a relative <c>data/keys/...</c>
    /// default works in both Development (relative to the working directory)
    /// and the published Docker image (relative to <c>/app/</c>). Resolves and
    /// writes the path back through <paramref name="setPath"/> even when the
    /// file already exists, so the caller always ends up with an absolute path.
    /// </summary>
    public static void EnsureCertificateExists(
        Func<string?> getPath,
        Action<string> setPath,
        string defaultRelativePath,
        string subject,
        string purpose,
        X509KeyUsageFlags keyUsage)
    {
        var configured = getPath();
        var path = string.IsNullOrWhiteSpace(configured)
            ? PathHelper.GetFullPath(defaultRelativePath)
            : PathHelper.GetFullPath(configured);
        setPath(path);

        if (File.Exists(path)) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        GenerateSelfSignedPfx(path, subject, keyUsage, validYears: 2, keySize: 2048);

        Log.Warning(
            "auto-generated self-signed {Purpose} certificate at {Path}. " +
            "This is fine for self-hosted Beta; replace with a managed cert " +
            "(Key Vault / Secrets Manager / cocoar-secrets generate-cert) before " +
            "going to public production.",
            purpose, path);
    }

    /// <summary>
    /// Inline self-signed-cert generator. We don't reuse
    /// <c>Cocoar.Configuration.X509Encryption.X509CertificateGenerator</c>
    /// because that helper is hardcoded for content-encryption use cases
    /// (KeyEncipherment + DataEncipherment) and OpenIddict's
    /// <c>AddSigningCertificate</c> rejects certs without
    /// <c>DigitalSignature</c> in their X509KeyUsage extension. Different
    /// purposes need different KeyUsage bits, so we generate ourselves and
    /// pass the flags in.
    ///
    /// <para>Output: passwordless PFX, file-system permissions restricted to
    /// owner read+write on Linux. Mirrors the cocoar-secrets CLI convention.</para>
    /// </summary>
    public static void GenerateSelfSignedPfx(
        string outputPath,
        string subject,
        X509KeyUsageFlags keyUsage,
        int validYears,
        int keySize)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(keyUsage, critical: false));

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddYears(validYears);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(outputPath, pfxBytes);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(outputPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
