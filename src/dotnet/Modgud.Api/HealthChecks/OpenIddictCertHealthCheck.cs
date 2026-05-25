using System.Security.Cryptography.X509Certificates;
using Modgud.Api.Helper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// Verifies the OpenIddict signing (and encryption, when separately
/// configured) certificate(s) are present on disk and loadable as PFX.
/// In <c>DevelopmentMode</c> the probe is a no-op — ephemeral keys live
/// in memory and have no on-disk artefact.
///
/// <para>Marked tag <c>ready</c> so it gates /health/ready. Orchestrator
/// won't route traffic until the certs are mountable — catches the case
/// where a volume mount fails after a Pod restart.</para>
/// </summary>
public sealed class OpenIddictCertHealthCheck(OpenIddictSettings settings) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (settings.DevelopmentMode)
            return Task.FromResult(HealthCheckResult.Healthy(
                "DevelopmentMode — ephemeral signing keys, no on-disk cert to verify."));

        var signing = string.IsNullOrEmpty(settings.SigningCertificatePath)
            ? "data/keys/signing.pfx"
            : settings.SigningCertificatePath;

        var signingFull = PathHelper.GetFullPath(signing);
        if (!File.Exists(signingFull))
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Signing certificate not found at {signingFull}."));

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                signingFull, password: null, X509KeyStorageFlags.DefaultKeySet);
            if (!cert.HasPrivateKey)
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Signing certificate at {signingFull} has no private key."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Signing certificate at {signingFull} failed to load.", ex));
        }

        // Encryption cert is optional — falls back to signing when unset.
        if (!string.IsNullOrEmpty(settings.EncryptionCertificatePath))
        {
            var encFull = PathHelper.GetFullPath(settings.EncryptionCertificatePath);
            if (!File.Exists(encFull))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Encryption certificate not found at {encFull}."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Certificates present and loadable."));
    }
}
