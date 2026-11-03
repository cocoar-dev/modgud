using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B6-SAML-02 — "Require encrypted assertions" (WantAssertionsEncrypted) was offered in
/// the admin UI and persisted, but neither the context builder nor the ACS used it: an
/// unencrypted assertion was accepted all the same. It is now enforced on the raw
/// SAMLResponse; an encrypted assertion for the SP's own certificate still signs in.
/// </summary>
public partial class SamlAcsReplayEndToEndTests
{
    [Fact]
    public async Task An_unencrypted_assertion_is_refused_when_encryption_is_required()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert, wantAssertionsEncrypted: true);
        await SeedLinkedUserAsync(provider.Id);

        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);
        var response = await PostToAcsAsync(http, MintSignedResponse(idpCert, requestId), ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=saml-assertion-not-encrypted", response.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(response));
        Assert.False((await LoadPendingRequestAsync(requestId, ct))!.IsConsumed);
    }

    [Fact]
    public async Task An_encrypted_assertion_signs_in_when_encryption_is_required()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert, wantAssertionsEncrypted: true);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);

        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);
        using var spCert = await SpEncryptionCertificateAsync(http, ct);
        var response = await PostToAcsAsync(http, MintSignedResponse(idpCert, requestId, encryptFor: spCert), ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        await AssertSignedInAsync(http, userId, ct);
    }

    /// <summary>The SP certificate the IdP encrypts for, as published in the SP metadata.</summary>
    private async Task<X509Certificate2> SpEncryptionCertificateAsync(HttpClient http, CancellationToken ct)
    {
        var metadata = XDocument.Parse(await http.GetStringAsync($"/saml/{_slug}/sp-metadata", ct));
        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        var descriptor = metadata.Descendants(md + "KeyDescriptor")
            .FirstOrDefault(k => (string?)k.Attribute("use") == "encryption")
            ?? metadata.Descendants(md + "KeyDescriptor").First();
        var certB64 = descriptor.Descendants(ds + "X509Certificate").First().Value.Trim();
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certB64));
    }
}
