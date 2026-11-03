using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B6-SAML-04 — for an IdP whose metadata offers only HTTP-POST single sign-on, the
/// metadata fetcher fell back to that POST URL, but the login always used the redirect
/// binding: the AuthnRequest went to a POST-only endpoint as a GET query, which such an
/// IdP refuses. The login now answers with the auto-submitting POST form for it.
/// </summary>
public partial class SamlAcsReplayEndToEndTests
{
    [Fact]
    public async Task A_post_only_idp_gets_the_authn_request_by_form_post()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert, postOnlySso: true);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);

        using var http = NewBrowser();
        var start = await http.GetAsync($"/saml/{_slug}/login?returnUrl=%2F%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E", ct);

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("text/html", start.Content.Headers.ContentType?.MediaType);
        var html = await start.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("<script>alert(1)", html); // RelayState is encoded
        var action = Regex.Match(html, "<form[^>]*action=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(action.Success, html);
        Assert.Equal(IdpSsoUrl, System.Net.WebUtility.HtmlDecode(action.Groups[1].Value));
        var csp = string.Join(";", start.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("form-action https://idp.test", csp);

        // The posted AuthnRequest is the one the flow recorded, and the login completes.
        var samlRequest = Regex.Match(html, "name=\"SAMLRequest\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        Assert.True(samlRequest.Success, html);
        var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            System.Net.WebUtility.HtmlDecode(samlRequest.Groups[1].Value))));
        var requestId = (string)document.Root!.Attribute("ID")!;
        Assert.NotNull(await LoadPendingRequestAsync(requestId, ct));
        var acs = await PostToAcsAsync(http, MintSignedResponse(idpCert, requestId), ct);
        Assert.Equal("/", acs.Headers.Location!.ToString());
        await AssertSignedInAsync(http, userId, ct);
    }
}
