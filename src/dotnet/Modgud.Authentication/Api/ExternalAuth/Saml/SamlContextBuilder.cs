using System.Security.Cryptography.X509Certificates;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

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
    /// Constructs the Saml2Configuration. Throws if the provider has no
    /// IdP metadata yet (no signing certs to validate against, no SSO URL
    /// to redirect to) — endpoint handlers should pre-check
    /// <see cref="RegisteredSamlProvider.IdpMetadata"/> and surface a 503
    /// instead of letting this throw deep in the lib.
    /// </summary>
    public async Task<Saml2Configuration> BuildAsync(
        RegisteredSamlProvider provider,
        CancellationToken ct = default)
    {
        if (provider.IdpMetadata is null)
            throw new InvalidOperationException(
                $"SAML provider {provider.LoginProviderId} has no IdP metadata cached. " +
                "Call the metadata refresh / paste XML before initiating SAML flows.");

        var idp = provider.IdpMetadata;
        var spCert = await spCertService.GetActiveAsync(ct);

        var spEntityId = BuildSpEntityId(provider.LoginProviderId);

        var config = new Saml2Configuration
        {
            Issuer = spEntityId,
            SigningCertificate = spCert,
            DecryptionCertificate = spCert,
            SignAuthnRequest = provider.FlavorData.SignAuthnRequest,
            AllowedIssuer = idp.EntityId,
            CertificateValidationMode =
                System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        config.AllowedAudienceUris.Add(spEntityId);

        foreach (var b64 in idp.SigningCertificatesBase64)
        {
            try
            {
                var cert = new X509Certificate2(Convert.FromBase64String(b64));
                config.SignatureValidationCertificates.Add(cert);
            }
            catch
            {
                // Skip invalid base64 entries — the metadata refresh path
                // populates these from XML, parse errors there go to log.
            }
        }

        return config;
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
