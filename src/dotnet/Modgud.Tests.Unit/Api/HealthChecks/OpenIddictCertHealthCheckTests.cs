using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modgud.Api;
using Modgud.Api.HealthChecks;

namespace Modgud.Tests.Unit.Api.HealthChecks;

/// <summary>
/// FK-04 — the global OpenIddict certificates are auto-generated with a two-year lifetime
/// and never renewed. The readiness probe must surface an approaching or past expiry
/// instead of reporting "present and loadable" until the day nothing works any more.
/// </summary>
public sealed class OpenIddictCertHealthCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modgud-certhc-" + Guid.NewGuid().ToString("N"));

    public OpenIddictCertHealthCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Certificates_well_within_their_validity_are_healthy()
    {
        var settings = new OpenIddictSettings
        {
            SigningCertificatePath = WriteCert("signing.pfx", TimeSpan.FromDays(-1), TimeSpan.FromDays(700)),
            EncryptionCertificatePath = WriteCert("encryption.pfx", TimeSpan.FromDays(-1), TimeSpan.FromDays(700)),
        };

        var result = await Check(settings);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task A_signing_certificate_close_to_expiry_degrades_the_probe()
    {
        var settings = new OpenIddictSettings
        {
            SigningCertificatePath = WriteCert("signing.pfx", TimeSpan.FromDays(-700), TimeSpan.FromDays(10)),
            EncryptionCertificatePath = WriteCert("encryption.pfx", TimeSpan.FromDays(-1), TimeSpan.FromDays(700)),
        };

        var result = await Check(settings);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Signing certificate", result.Description);
        Assert.Contains("expires", result.Description);
    }

    [Fact]
    public async Task An_expired_signing_certificate_degrades_the_probe()
    {
        var settings = new OpenIddictSettings
        {
            SigningCertificatePath = WriteCert("signing.pfx", TimeSpan.FromDays(-730), TimeSpan.FromDays(-1)),
            EncryptionCertificatePath = WriteCert("encryption.pfx", TimeSpan.FromDays(-1), TimeSpan.FromDays(700)),
        };

        var result = await Check(settings);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("expired", result.Description);
    }

    [Fact]
    public async Task An_encryption_certificate_close_to_expiry_degrades_the_probe()
    {
        var settings = new OpenIddictSettings
        {
            SigningCertificatePath = WriteCert("signing.pfx", TimeSpan.FromDays(-1), TimeSpan.FromDays(700)),
            EncryptionCertificatePath = WriteCert("encryption.pfx", TimeSpan.FromDays(-700), TimeSpan.FromDays(5)),
        };

        var result = await Check(settings);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Encryption certificate", result.Description);
    }

    private static Task<HealthCheckResult> Check(OpenIddictSettings settings) =>
        new OpenIddictCertHealthCheck(settings).CheckHealthAsync(new HealthCheckContext());

    private string WriteCert(string name, TimeSpan notBeforeOffset, TimeSpan notAfterOffset)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Modgud Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now + notBeforeOffset, now + notAfterOffset);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return path;
    }
}
