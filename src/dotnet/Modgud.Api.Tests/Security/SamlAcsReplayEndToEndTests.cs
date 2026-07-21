using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ErrorOr;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.Saml;
using Modgud.Infrastructure.Persistence.Tenancy;
using Wolverine;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// End-to-end SAML replay protection through the REAL HTTP endpoints (P0-2).
///
/// <para>
/// <see cref="SamlReplayProtectionTests"/> pins the store in isolation. This one
/// closes the gap that class's doc called out: it drives <c>GET /saml/{slug}/login</c>
/// and <c>POST /saml/{slug}/acs</c> over HTTP, with a self-hosted test IdP that
/// mints a genuinely signed SAML Response. That is what proves the two things the
/// store-level test cannot: that the AuthnRequest ID the flow persists is the exact
/// ID that rides <c>InResponseTo</c> on the way back (a mismatch would break every
/// SAML login, not just replays), and that the ACS actually consumes it.
/// </para>
///
/// <para>
/// The IdP side is ITfoxtec itself, configured with a throwaway signing cert whose
/// public half is published in the metadata XML the SP validates against — the same
/// library on both ends, so the signature is real, not stubbed.
/// </para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SamlAcsReplayEndToEndTests : IntegrationTestBase
{
    public SamlAcsReplayEndToEndTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Slug = "e2e-idp";
    private const string IdpEntityId = "urn:test:e2e-idp";
    private const string IdpSsoUrl = "https://idp.test/sso";
    private const string SubjectNameId = "e2e-user@test.com";

    // http://localhost is the WebApplicationFactory default base address, so
    // RealmMiddleware resolves these requests to the seeded "system" realm and the
    // SP entity id / ACS url match what SamlContextBuilder derives from the request.
    private static string SpEntityId => $"http://localhost/saml/{Slug}/sp-metadata";
    private static string AcsUrl => $"http://localhost/saml/{Slug}/acs";

    [Fact]
    public async Task Happy_path_consumes_the_request_and_replay_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        await CreateAndRegisterProviderAsync(idpCert);

        var http = Factory.CreateDefaultClient(); // does not auto-follow redirects

        // 1. SP-initiated login. The flow persists the AuthnRequest it generates.
        var login = await http.GetAsync($"/saml/{Slug}/login?returnUrl=%2F", ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith(IdpSsoUrl, login.Headers.Location!.ToString());

        // The persisted request is the source of the ID we must echo. Reading it
        // (rather than parsing the deflated SAMLRequest) also asserts the flow
        // recorded exactly one pending request.
        var requestId = await SinglePendingRequestIdAsync(ct);

        // 2. The IdP mints a signed Response answering that exact request.
        var samlResponse = MintSignedResponse(idpCert, requestId);

        // 3. First presentation: must get PAST correlation and consume the request.
        var first = await PostToAcsAsync(http, samlResponse, ct);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        AssertNotACorrelationRejection(first.Headers.Location!.ToString());

        var consumed = await LoadPendingRequestAsync(requestId, ct);
        Assert.NotNull(consumed);
        Assert.True(consumed!.IsConsumed,
            "the ACS must consume the pending request when InResponseTo matches");

        // 4. Replay the very same captured Response. Now the request is spent.
        var replay = await PostToAcsAsync(http, samlResponse, ct);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Contains("error=saml-replayed", replay.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_signed_response_with_an_unknown_InResponseTo_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        await CreateAndRegisterProviderAsync(idpCert);

        var http = Factory.CreateDefaultClient();

        // A correctly-signed Response — but referencing a request this SP never
        // sent. It must clear signature validation and then be refused at
        // correlation, which is what proves the two checks are both present and
        // correctly ordered (signature first, correlation second).
        var samlResponse = MintSignedResponse(idpCert, inResponseTo: $"_id{Guid.NewGuid():N}");

        var result = await PostToAcsAsync(http, samlResponse, ct);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Contains("error=saml-unknown-request", result.Headers.Location!.ToString());
    }

    // ── arrange helpers ──────────────────────────────────────────────────────

    private async Task CreateAndRegisterProviderAsync(X509Certificate2 idpCert)
    {
        var flavorData = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            metadataXml = BuildIdpMetadataXml(idpCert),
        }));

        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericSaml,
            DisplayName: "E2E SAML",
            Slug: Slug,
            FlavorData: flavorData,
            Type: LoginProviderType.Saml,
            Enabled: true));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");

        // The Wolverine event cascade that normally registers the provider runs on
        // a background pump with no drain hook in tests, so register explicitly —
        // the same pattern DynamicOidcSchemeManagerTests uses.
        var manager = scope.ServiceProvider.GetRequiredService<DynamicSamlSchemeManager>();
        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(result.Value);
    }

    private async Task<string> SinglePendingRequestIdAsync(CancellationToken ct)
    {
        await using var qs = GetTenantedSession();
        var pending = await qs.Query<SamlPendingAuthnRequest>().ToListAsync(ct);
        return Assert.Single(pending).Id;
    }

    private async Task<SamlPendingAuthnRequest?> LoadPendingRequestAsync(string id, CancellationToken ct)
    {
        await using var qs = GetTenantedSession();
        return await qs.LoadAsync<SamlPendingAuthnRequest>(id, ct);
    }

    private static async Task<HttpResponseMessage> PostToAcsAsync(
        HttpClient http, string samlResponseBase64, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("SAMLResponse", samlResponseBase64),
            new KeyValuePair<string, string>("RelayState", "/"),
        });
        return await http.PostAsync($"/saml/{Slug}/acs", form, ct);
    }

    private static void AssertNotACorrelationRejection(string location)
    {
        foreach (var code in new[]
        {
            "saml-unsolicited", "saml-replayed", "saml-expired",
            "saml-unknown-request", "saml-provider-mismatch",
        })
        {
            Assert.DoesNotContain(code, location);
        }
    }

    // ── test IdP ─────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateIdpSigningCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=modgud-e2e-test-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        var cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        // Round-trip through PKCS#12 so the returned cert keeps an exportable
        // private key usable for XML signing.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    private static string BuildIdpMetadataXml(X509Certificate2 idpCert)
    {
        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        var certB64 = Convert.ToBase64String(idpCert.Export(X509ContentType.Cert));

        var doc = new XDocument(
            new XElement(md + "EntityDescriptor",
                new XAttribute("entityID", IdpEntityId),
                new XAttribute(XNamespace.Xmlns + "md", md.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "ds", ds.NamespaceName),
                new XElement(md + "IDPSSODescriptor",
                    new XAttribute("protocolSupportEnumeration", "urn:oasis:names:tc:SAML:2.0:protocol"),
                    new XElement(md + "KeyDescriptor",
                        new XAttribute("use", "signing"),
                        new XElement(ds + "KeyInfo",
                            new XElement(ds + "X509Data",
                                new XElement(ds + "X509Certificate", certB64)))),
                    new XElement(md + "SingleSignOnService",
                        new XAttribute("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"),
                        new XAttribute("Location", IdpSsoUrl)))));

        return doc.ToString();
    }

    private static string MintSignedResponse(X509Certificate2 idpCert, string inResponseTo)
    {
        var idpConfig = new Saml2Configuration
        {
            Issuer = IdpEntityId,
            SigningCertificate = idpCert,
            // The SP's default floor is WantAssertionsSigned=true, which requires a
            // per-assertion ds:Signature. ITfoxtec signs the Response wrapper by
            // default, so ask it to sign the assertion instead.
            AuthnResponseSignType = Saml2AuthnResponseSignTypes.SignAssertion,
        };

        var response = new Saml2AuthnResponse(idpConfig)
        {
            InResponseToAsString = inResponseTo,
            Status = Saml2StatusCodes.Success,
            Destination = new Uri(AcsUrl),
            NameId = new Microsoft.IdentityModel.Tokens.Saml2.Saml2NameIdentifier(SubjectNameId),
        };
        response.ClaimsIdentity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, SubjectNameId),
            new Claim("email", SubjectNameId),
        });

        // appliesToAddress = the SP entity id => assertion audience. ITfoxtec signs
        // the assertion during Bind using the config's SigningCertificate, which is
        // what the SP's WantAssertionsSigned floor (default on) requires.
        response.CreateSecurityToken(SpEntityId);

        var binding = new Saml2PostBinding { RelayState = "/" };
        binding.Bind(response);
        return ExtractSamlResponseField(binding.PostContent);
    }

    private static string ExtractSamlResponseField(string postContent)
    {
        var match = Regex.Match(postContent,
            "name=\"SAMLResponse\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "could not find SAMLResponse field in the bound POST content");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
