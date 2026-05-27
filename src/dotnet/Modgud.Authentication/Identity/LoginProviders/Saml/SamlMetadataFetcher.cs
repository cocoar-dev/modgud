using System.Xml;
using System.Xml.Linq;

namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Fetches IdP federation-metadata XML over HTTP and extracts the bits we
/// actually use: EntityID, signing certificates, SSO endpoint URL. We do
/// the XML reading ourselves with <see cref="XDocument"/> rather than
/// pulling ITfoxtec's <c>Saml2MetadataReader</c> here because the latter
/// builds a richer model than we need (NameID formats, attribute-consuming
/// services, contact persons) — the surface we need is small enough that
/// 30 lines of LINQ-to-XML are cheaper than a tight coupling to the lib's
/// metadata-model evolution.
/// <para>
/// This fetcher is consumed by:
/// </para>
/// <list type="bullet">
///   <item>Task #14 ACS / login flow — first-time fetch on cache miss
///         when an admin saves a provider config and we have no certs yet.</item>
///   <item>Task #15 background refresh job — periodic re-fetch per the
///         per-provider <c>MetadataRefreshIntervalSeconds</c> cadence.</item>
/// </list>
/// </summary>
public class SamlMetadataFetcher
{
    private static readonly XNamespace MdNs = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace DsNs = "http://www.w3.org/2000/09/xmldsig#";
    private const string HttpRedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    private const string HttpPostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    public const string HttpClientName = "Modgud.Saml.MetadataFetcher";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SamlMetadataFetcher> _logger;

    public SamlMetadataFetcher(IHttpClientFactory httpFactory, ILogger<SamlMetadataFetcher> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches metadata from the URL and parses out the SP-side relevant fields.
    /// Returns null on any failure (network, parse, missing required fields) —
    /// callers log + carry on with the stale cached data rather than crashing
    /// the login flow.
    /// </summary>
    public async Task<SamlIdpMetadata?> FetchAsync(string metadataUrl, CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(metadataUrl, ct);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(ct);
            return Parse(xml);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auth: SAML metadata fetch failed for {Url}", metadataUrl);
            return null;
        }
    }

    /// <summary>
    /// Parses pre-fetched metadata XML (the alternative ingress path for
    /// firewalled IdPs that don't publish a reachable URL — admin pastes the
    /// XML into the FlavorData.MetadataXml field).
    /// </summary>
    public SamlIdpMetadata? Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var entityDescriptor = doc.Root;
            if (entityDescriptor is null || entityDescriptor.Name != MdNs + "EntityDescriptor")
                return null;

            var entityId = entityDescriptor.Attribute("entityID")?.Value;
            if (string.IsNullOrWhiteSpace(entityId)) return null;

            var idpSsoDescriptor = entityDescriptor.Element(MdNs + "IDPSSODescriptor");
            if (idpSsoDescriptor is null)
            {
                // Not an IdP metadata document.
                return null;
            }

            var signingCerts = idpSsoDescriptor
                .Elements(MdNs + "KeyDescriptor")
                .Where(kd => kd.Attribute("use") is null
                    || string.Equals(kd.Attribute("use")?.Value, "signing", StringComparison.OrdinalIgnoreCase))
                .SelectMany(kd => kd.Descendants(DsNs + "X509Certificate"))
                .Select(c => Normalize(c.Value))
                .Where(s => s.Length > 0)
                .ToArray();

            // SingleSignOnService — prefer Redirect binding (the SP-initiated
            // login redirect we'll generate). Fall back to POST if Redirect
            // isn't advertised (rare).
            var ssoBindings = idpSsoDescriptor
                .Elements(MdNs + "SingleSignOnService")
                .ToArray();

            var ssoRedirect = ssoBindings
                .FirstOrDefault(s => string.Equals(s.Attribute("Binding")?.Value, HttpRedirectBinding, StringComparison.Ordinal))
                ?.Attribute("Location")?.Value;

            var ssoPost = ssoBindings
                .FirstOrDefault(s => string.Equals(s.Attribute("Binding")?.Value, HttpPostBinding, StringComparison.Ordinal))
                ?.Attribute("Location")?.Value;

            return new SamlIdpMetadata(
                EntityId: entityId,
                SigningCertificatesBase64: signingCerts,
                SsoRedirectUrl: ssoRedirect,
                SsoPostUrl: ssoPost);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Auth: SAML metadata XML parse failed");
            return null;
        }
    }

    // Strips PEM-style whitespace and line breaks; some IdPs publish certs
    // pretty-printed and some flat, both are valid as base64.
    private static string Normalize(string raw) =>
        new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
}

/// <summary>
/// Extracted IdP metadata in the shape we care about. Stored back into
/// <c>SamlFlavorData.SigningCertificates</c> + the registered-provider
/// cache after each successful fetch.
/// </summary>
public sealed record SamlIdpMetadata(
    string EntityId,
    IReadOnlyList<string> SigningCertificatesBase64,
    string? SsoRedirectUrl,
    string? SsoPostUrl);
