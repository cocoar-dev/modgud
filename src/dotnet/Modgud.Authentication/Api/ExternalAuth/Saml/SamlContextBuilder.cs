using System.Security.Cryptography.X509Certificates;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Disposable wrapper around a per-request <see cref="Saml2Configuration"/>
/// that owns the cert handles ITfoxtec consumes. <see cref="Saml2Configuration"/>
/// is not <see cref="IDisposable"/> and the library doesn't dispose certs
/// it borrowed via SigningCertificate / DecryptionCertificates /
/// SignatureValidationCertificates, so under sustained SAML load the
/// native CNG/CAPI key handles would otherwise leak until GC finalisers
/// catch up. Call-sites use <c>using var ctx = await builder.BuildAsync(...)</c>
/// so the cert handles are returned promptly.
/// </summary>
public sealed class Saml2RequestContext : IDisposable
{
    private readonly List<X509Certificate2> _ownedCerts;
    public Saml2Configuration Configuration { get; }

    public Saml2RequestContext(Saml2Configuration config, IEnumerable<X509Certificate2> ownedCerts)
    {
        Configuration = config;
        _ownedCerts = ownedCerts.ToList();
    }

    public void Dispose()
    {
        foreach (var c in _ownedCerts)
        {
            try { c.Dispose(); }
            catch { /* defensive — disposal never throws */ }
        }
        _ownedCerts.Clear();
    }
}

/// <summary>
/// Builds the per-request <see cref="Saml2Configuration"/> that ITfoxtec.
/// Identity.Saml2 consumes for AuthnRequest signing + Response validation.
/// Bridges the cached <see cref="RegisteredSamlProvider"/> + parsed IdP
/// metadata + per-realm SP cert into the shape the lib expects.
/// <para>
/// One configuration per (provider, request) — built fresh each time
/// rather than cached, because the SP cert can rotate at any moment and
/// caching would risk using a stale signing key after a rotation.
/// </para>
/// </summary>
public class SamlContextBuilder(
    SamlSpCertificateService spCertService,
    IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// Constructs the SAML request context. Throws if the provider has no
    /// IdP metadata yet (no signing certs to validate against, no SSO URL
    /// to redirect to) — endpoint handlers should pre-check
    /// <see cref="RegisteredSamlProvider.IdpMetadata"/> and surface a 503
    /// instead of letting this throw deep in the lib. The returned context
    /// owns the cert handles — caller MUST dispose it (use <c>using</c>).
    /// </summary>
    public async Task<Saml2RequestContext> BuildAsync(
        RegisteredSamlProvider provider,
        CancellationToken ct = default)
    {
        if (provider.IdpMetadata is null)
            throw new InvalidOperationException(
                $"SAML provider {provider.LoginProviderId} has no IdP metadata cached. " +
                "Call the metadata refresh / paste XML before initiating SAML flows.");

        var idp = provider.IdpMetadata;
        // Plural decryption-cert list spans the rotation-overlap window —
        // an IdP that hasn't refreshed metadata yet may still encrypt to the
        // PREVIOUS SP cert during overlap, so ITfoxtec must try both. We
        // sign outgoing AuthnRequests with the active cert only (otherwise
        // we'd send a confusing dual-signature mess).
        var decryptionCerts = await spCertService.GetDecryptionCertsAsync(ct);
        var signingCert = decryptionCerts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"SAML SP active cert is missing for provider {provider.LoginProviderId}.");

        var spEntityId = BuildSpEntityId(provider.LoginProviderId);

        var config = new Saml2Configuration
        {
            Issuer = spEntityId,
            SigningCertificate = signingCert,
            DecryptionCertificates = [.. decryptionCerts],
            SignAuthnRequest = provider.FlavorData.SignAuthnRequest,
            AllowedIssuer = idp.EntityId,
            CertificateValidationMode =
                System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        config.AllowedAudienceUris.Add(spEntityId);

        var ownedCerts = new List<X509Certificate2>(decryptionCerts);

        foreach (var b64 in idp.SigningCertificatesBase64)
        {
            try
            {
                var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64));
                config.SignatureValidationCertificates.Add(cert);
                ownedCerts.Add(cert);
            }
            catch
            {
                // Skip invalid base64 entries — the metadata refresh path
                // populates these from XML, parse errors there go to log.
            }
        }

        return new Saml2RequestContext(config, ownedCerts);
    }

    /// <summary>
    /// Our SP EntityID for a given provider — also used as the audience the
    /// IdP must address in the assertion. Form: scheme + host + SP-metadata
    /// path of the specific provider, matching what the SP metadata XML
    /// publishes.
    /// </summary>
    public string BuildSpEntityId(Guid providerId)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "SamlContextBuilder requires an HttpContext to derive the SP EntityID.");
        var req = http.Request;
        return $"{req.Scheme}://{req.Host}/saml/{providerId:D}/sp-metadata";
    }

    /// <summary>
    /// Our ACS URL for a given provider — where the IdP form-POSTs the
    /// SAMLResponse. Must match what we advertise in SP metadata.
    /// </summary>
    public string BuildAcsUrl(Guid providerId)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "SamlContextBuilder requires an HttpContext to derive the ACS URL.");
        var req = http.Request;
        return $"{req.Scheme}://{req.Host}/saml/{providerId:D}/acs";
    }
}
