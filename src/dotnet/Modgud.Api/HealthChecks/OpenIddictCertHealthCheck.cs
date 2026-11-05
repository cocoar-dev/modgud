using System.Security.Cryptography.X509Certificates;
using Modgud.Api.Helper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modgud.Api.HealthChecks;

/// <summary>
/// Verifies the OpenIddict signing (and encryption, when separately
/// configured) certificate(s) are present on disk and loadable as PFX, and
/// reports Degraded when one of them expires within
/// <see cref="ExpiryWarningDays"/> days or has already expired (FK-04).
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

        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                signingFull, password: null, X509KeyStorageFlags.DefaultKeySet);
            if (!cert.HasPrivateKey)
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Signing certificate at {signingFull} has no private key."));
            AddExpiryWarning(warnings, "Signing", signingFull, cert, now);
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

            try
            {
                using var enc = X509CertificateLoader.LoadPkcs12FromFile(
                    encFull, password: null, X509KeyStorageFlags.DefaultKeySet);
                AddExpiryWarning(warnings, "Encryption", encFull, enc, now);
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Encryption certificate at {encFull} failed to load.", ex));
            }
        }

        // FK-04 — the auto-generated certificates live two years and are never renewed.
        // OpenIddict refuses to configure itself once every certificate on an axis has
        // expired (ID0087/ID0088), so the next restart would fail. An approaching or past
        // expiry degrades the probe (logged as a warning by the health-check service,
        // visible in the /health JSON) without taking the running instance out of
        // rotation: it still works with the options it built at start, and every instance
        // shares the same files, so failing readiness would turn the warning into an outage.
        if (warnings.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(string.Join(" ", warnings)));

        return Task.FromResult(HealthCheckResult.Healthy("Certificates present and loadable."));
    }

    /// <summary>Days before <c>NotAfter</c> from which the probe reports Degraded.</summary>
    public const int ExpiryWarningDays = 30;

    private static void AddExpiryWarning(
        List<string> warnings, string purpose, string path, X509Certificate2 cert, DateTimeOffset now)
    {
        var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (notAfter <= now)
        {
            warnings.Add($"{purpose} certificate at {path} expired on {notAfter:yyyy-MM-dd}; this instance keeps running, but a restart fails (OpenIddict refuses expired certificates) — replace it now (see docs/operate/key-material.md).");
            return;
        }

        var remaining = notAfter - now;
        if (remaining <= TimeSpan.FromDays(ExpiryWarningDays))
            warnings.Add($"{purpose} certificate at {path} expires on {notAfter:yyyy-MM-dd} (in {(int)remaining.TotalDays} days); replace it before then (see docs/operate/key-material.md).");
    }
}
