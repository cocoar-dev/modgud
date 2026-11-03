using System.Net;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ErrorOr;
using BuildingBlocks.Helper;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Domain.Users.Events;
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

    // The shared host's provider cache outlives each test's DB reset. Distinct
    // slugs avoid resolving a prior test's cached provider by FirstOrDefault.
    private readonly string _slug = $"e2e-idp-{Guid.NewGuid():N}";
    private const string IdpEntityId = "urn:test:e2e-idp";
    private const string IdpSsoUrl = "https://idp.test/sso";
    private const string SubjectNameId = "e2e-user@test.com";

    // http://localhost is the WebApplicationFactory default base address, so
    // RealmMiddleware resolves these requests to the seeded "system" realm and the
    // SP entity id / ACS url match what SamlContextBuilder derives from the request.
    private string SpEntityId => $"http://localhost/saml/{_slug}/sp-metadata";
    private string AcsUrl => $"http://localhost/saml/{_slug}/acs";

    [Theory]
    [InlineData(false, Saml2AuthnResponseSignTypes.SignAssertion)]
    [InlineData(true, Saml2AuthnResponseSignTypes.SignAssertion)]
    [InlineData(false, Saml2AuthnResponseSignTypes.SignResponse)]
    [InlineData(false, Saml2AuthnResponseSignTypes.SignAssertionAndResponse)]
    public async Task Happy_path_consumes_the_request_and_replay_is_refused(
        bool additionalAttributes, Saml2AuthnResponseSignTypes signType)
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert, signType);
        var (userId, linkId) = await SeedLinkedUserAsync(provider.Id);

        using var http = NewBrowser(); // no pre-seeded password/application cookie
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/account/me", ct)).StatusCode);

        // 1. SP-initiated login. The flow persists the AuthnRequest it generates.
        var requestId = await StartRequestAsync(http, provider.Id, ct);

        // 2. The IdP mints a signed Response answering that exact request.
        var samlResponse = MintSignedResponse(idpCert, requestId, additionalAttributes, signType);

        // 3. First presentation: must get PAST correlation and consume the request.
        var first = await PostToAcsAsync(http, samlResponse, ct);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/", first.Headers.Location!.ToString());
        Assert.True(HasApplicationCookie(first));
        await AssertSignedInAsync(http, userId, ct);
        await AssertSignedInAsync(http, userId, ct); // includes security-stamp revalidation

        var consumed = await LoadPendingRequestAsync(requestId, ct);
        Assert.NotNull(consumed);
        Assert.True(consumed!.IsConsumed,
            "the ACS must consume the pending request when InResponseTo matches");

        // 4. Replay the very same captured Response. Now the request is spent.
        using var secondBrowser = NewBrowser();
        var replay = await PostToAcsAsync(secondBrowser, samlResponse, ct);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Contains("error=saml-replayed", replay.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondBrowser.GetAsync("/api/account/me", ct)).StatusCode);
        var afterReplay = await LoadPendingRequestAsync(requestId, ct);
        Assert.Equal(consumed.ConsumedAt, afterReplay!.ConsumedAt);
        await using var fresh = GetTenantedDocumentSession();
        Assert.Equal(SamlAuthnRequestConsumeResult.AlreadyConsumed,
            await new MartenSamlAuthnRequestStore(fresh).TryConsumeAsync(requestId, provider.Id, ct));
        var link = await fresh.LoadAsync<ExternalIdentityLink>(linkId, ct);
        Assert.NotNull(link);
        Assert.Equal(userId, link.UserId);
        Assert.Equal(provider.Id, link.LoginProviderId);
        Assert.Equal(IdpEntityId, link.Issuer);
        Assert.Equal(SubjectNameId, link.Subject);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_xml_or_signature_does_not_consume_the_pending_request(bool tamperSignature)
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);
        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);

        string xml;
        if (tamperSignature)
        {
            var original = MintSignedResponse(idpCert, requestId);
            var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(original)),
                LoadOptions.PreserveWhitespace);
            document.Descendants().Single(element => element.Name.LocalName == "NameID").Value = "changed@example.test";
            xml = document.ToString(SaveOptions.DisableFormatting);
        }
        else
        {
            // Small malformed document, not an expansion/depth/CPU stress payload.
            xml = "<Response>";
        }

        var result = await PostToAcsAsync(http, Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)), ct);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/login?error=saml-invalid", result.Headers.Location!.ToString());
        var pending = await LoadPendingRequestAsync(requestId, ct);
        Assert.NotNull(pending);
        Assert.False(pending.IsConsumed);
        Assert.False(HasApplicationCookie(result));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/account/me", ct)).StatusCode);
        await AssertValidRetryAsync(http, idpCert, requestId, userId, ct);
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("wrong-cert")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("recipient")]
    [InlineData("destination")]
    [InlineData("assertion-issuer")]
    [InlineData("response-issuer")]
    [InlineData("signed-request")]
    [InlineData("confirmation-expired")]
    [InlineData("confirmation-future")]
    [InlineData("confirmation-no-request")]
    [InlineData("confirmation-no-recipient")]
    [InlineData("confirmation-no-expiry")]
    [InlineData("split-confirmations")]
    public async Task Invalid_signed_or_unsigned_input_is_refused_before_consumption_and_allows_valid_retry(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        using var idpCert = CreateIdpSigningCert();
        using var wrongCert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(idpCert);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);
        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);
        var encoded = MintSignedResponse(fault == "wrong-cert" ? wrongCert : idpCert, requestId,
            mutateBeforeSigning: response =>
            {
                var assertion = response.Saml2SecurityToken.Assertion;
                var data = assertion.Subject.SubjectConfirmations.First().SubjectConfirmationData;
                var now = DateTime.UtcNow;
                switch (fault)
                {
                    case "audience":
                        assertion.Conditions.AudienceRestrictions.First().Audiences.Clear();
                        assertion.Conditions.AudienceRestrictions.First().Audiences.Add("urn:test:other-sp");
                        break;
                    case "expired":
                        assertion.Conditions.NotBefore = now.AddMinutes(-20);
                        assertion.Conditions.NotOnOrAfter = now.AddMinutes(-10);
                        break;
                    case "future":
                        assertion.Conditions.NotBefore = now.AddMinutes(10);
                        assertion.Conditions.NotOnOrAfter = now.AddMinutes(20);
                        break;
                    case "recipient": data.Recipient = new Uri("https://other.example.test/acs"); break;
                    case "destination": response.Destination = new Uri("https://other.example.test/acs"); break;
                    case "assertion-issuer":
                        assertion.Issuer = new Microsoft.IdentityModel.Tokens.Saml2.Saml2NameIdentifier("urn:test:other-idp");
                        break;
                    case "response-issuer": response.Issuer = "urn:test:other-idp"; break;
                    case "signed-request": data.InResponseTo = new Microsoft.IdentityModel.Tokens.Saml2.Saml2Id("_different-request"); break;
                    case "confirmation-expired": data.NotOnOrAfter = now.AddMinutes(-10); break;
                    case "confirmation-future":
                        data.NotBefore = now.AddMinutes(10);
                        data.NotOnOrAfter = now.AddMinutes(20);
                        break;
                    case "confirmation-no-request": data.InResponseTo = null; break;
                    case "confirmation-no-recipient":
                        // Recipient's setter rejects null; leave it unset in a
                        // new confirmation so the signed XML really omits it.
                        assertion.Subject.SubjectConfirmations.First().SubjectConfirmationData =
                            new Microsoft.IdentityModel.Tokens.Saml2.Saml2SubjectConfirmationData
                            {
                                InResponseTo = data.InResponseTo,
                                NotBefore = data.NotBefore,
                                NotOnOrAfter = data.NotOnOrAfter,
                            };
                        break;
                    case "confirmation-no-expiry": data.NotOnOrAfter = null; break;
                    case "split-confirmations":
                        data.Recipient = new Uri("https://other.example.test/acs");
                        assertion.Subject.SubjectConfirmations.Add(new Microsoft.IdentityModel.Tokens.Saml2.Saml2SubjectConfirmation(
                            Saml2Constants.Saml2BearerToken,
                            new Microsoft.IdentityModel.Tokens.Saml2.Saml2SubjectConfirmationData
                            {
                                Recipient = new Uri(AcsUrl),
                                InResponseTo = new Microsoft.IdentityModel.Tokens.Saml2.Saml2Id("_different-request"),
                                NotOnOrAfter = now.AddMinutes(5),
                            }));
                        break;
                }
            });
        if (fault == "confirmation-no-recipient")
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var confirmation = Assert.Single(document.Descendants(
                XName.Get("SubjectConfirmationData", "urn:oasis:names:tc:SAML:2.0:assertion")));
            Assert.Null(confirmation.Attribute("Recipient"));
            Assert.Single(document.Descendants(XName.Get("Signature", "http://www.w3.org/2000/09/xmldsig#")));
        }
        if (fault == "unsigned")
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), LoadOptions.PreserveWhitespace);
            document.Descendants(XName.Get("Signature", "http://www.w3.org/2000/09/xmldsig#")).Remove();
            encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        }

        var rejected = await PostToAcsAsync(http, encoded, ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Equal("/login?error=saml-invalid", rejected.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(rejected));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/account/me", ct)).StatusCode);
        var pending = await LoadPendingRequestAsync(requestId, ct);
        Assert.NotNull(pending);
        Assert.False(pending.IsConsumed);
        await AssertValidRetryAsync(http, idpCert, requestId, userId, ct);
    }

    [Theory]
    [InlineData(Saml2AuthnResponseSignTypes.SignAssertion, Saml2AuthnResponseSignTypes.SignResponse, "assertion-unsigned")]
    [InlineData(Saml2AuthnResponseSignTypes.SignResponse, Saml2AuthnResponseSignTypes.SignAssertion, "response-unsigned")]
    public async Task A_valid_signature_cannot_replace_the_configured_signature_placement(
        Saml2AuthnResponseSignTypes required, Saml2AuthnResponseSignTypes actual, string error)
    {
        var ct = TestContext.Current.CancellationToken;
        using var cert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(cert, required);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);
        using var http = NewBrowser();
        var requestId = await StartRequestAsync(http, provider.Id, ct);
        var rejected = await PostToAcsAsync(http, MintSignedResponse(cert, requestId, signType: actual), ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Equal($"/login?error=saml-{error}", rejected.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(rejected));
        Assert.False((await LoadPendingRequestAsync(requestId, ct))!.IsConsumed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/account/me", ct)).StatusCode);
        await AssertValidRetryAsync(http, cert, requestId, userId, ct, required);
    }

    [Fact]
    public async Task An_unsigned_wrapper_cannot_retarget_a_signed_assertion_to_another_pending_request()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(cert);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);
        using var http = NewBrowser();
        var originalId = await StartRequestAsync(http, provider.Id, ct);
        var otherId = await StartRequestAsync(http, provider.Id, ct);
        var encoded = MintSignedResponse(cert, originalId);
        var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), LoadOptions.PreserveWhitespace);
        document.Root!.SetAttributeValue("InResponseTo", otherId); // outside the signed assertion
        var rejected = await PostToAcsAsync(http,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))), ct);
        Assert.Equal("/login?error=saml-invalid", rejected.Headers.Location!.ToString());
        Assert.False(HasApplicationCookie(rejected));
        Assert.False((await LoadPendingRequestAsync(originalId, ct))!.IsConsumed);
        Assert.False((await LoadPendingRequestAsync(otherId, ct))!.IsConsumed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/account/me", ct)).StatusCode);
        await AssertValidRetryAsync(http, cert, originalId, userId, ct);
    }

    [Fact]
    public async Task A_missing_optional_destination_keeps_the_signed_recipient_binding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cert = CreateIdpSigningCert();
        var provider = await CreateAndRegisterProviderAsync(cert);
        var (userId, _) = await SeedLinkedUserAsync(provider.Id);
        using var http = NewBrowser();
        var id = await StartRequestAsync(http, provider.Id, ct);
        var response = await PostToAcsAsync(http, MintSignedResponse(cert, id,
            mutateBeforeSigning: saml => saml.Destination = null), ct);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.True(HasApplicationCookie(response));
        await AssertSignedInAsync(http, userId, ct);
    }

    // ── arrange helpers ──────────────────────────────────────────────────────

    private async Task<LoginProvider> CreateAndRegisterProviderAsync(X509Certificate2 idpCert,
        Saml2AuthnResponseSignTypes signType = Saml2AuthnResponseSignTypes.SignAssertion)
    {
        var flavorData = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            metadataXml = BuildIdpMetadataXml(idpCert),
            wantAssertionsSigned = signType != Saml2AuthnResponseSignTypes.SignResponse,
            wantResponseSigned = signType != Saml2AuthnResponseSignTypes.SignAssertion,
        }));

        using var scope = Factory.Services.CreateScope();
        var bus = GetTenantedMessageBus(scope);
        var result = await bus.InvokeAsync<ErrorOr<LoginProvider>>(new CreateLoginProviderCommand(
            Flavor: LoginProviderFlavor.GenericSaml,
            DisplayName: "E2E SAML",
            Slug: _slug,
            FlavorData: flavorData,
            Type: LoginProviderType.Saml,
            Enabled: true,
            AutoCreateUsers: false,
            TrustForEmailLink: false,
            TrustForAuthorization: false,
            AuthoritativeForProfile: false,
            StoreRawClaims: false));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : "");

        // The Wolverine event cascade that normally registers the provider runs on
        // a background pump with no drain hook in tests, so register explicitly —
        // the same pattern DynamicOidcSchemeManagerTests uses.
        var manager = scope.ServiceProvider.GetRequiredService<DynamicSamlSchemeManager>();
        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(result.Value);
        return result.Value;
    }

    private async Task<(Guid UserId, Guid LinkId)> SeedLinkedUserAsync(Guid providerId)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(firstname: "Saml", lastname: "User",
            acronym: "SAMLUSER", email: SubjectNameId, isRealmAdmin: false);
        var linkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var session = GetTenantedDocumentSession();
        session.Events.StartStream<ExternalIdentityLink>(linkId, new ExternalIdentityLinkedEvent(
            Id: linkId, UserId: user.Id, LoginProviderId: providerId, Issuer: IdpEntityId,
            Subject: SubjectNameId, Email: null, DisplayName: null, LinkedAt: now, IsCreator: false));
        session.Events.Append(user.Id, new UserExternalIdentityLinkedEvent(user.Id, linkId, providerId, IdpEntityId, now));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (user.Id, linkId);
    }

    private HttpClient NewBrowser() => Factory.CreateDefaultClient(new CookieContainerHandler());

    private async Task<string> StartRequestAsync(HttpClient http, Guid providerId, CancellationToken ct)
    {
        var response = await http.GetAsync($"/saml/{_slug}/login?returnUrl=%2F", ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(IdpSsoUrl, response.Headers.Location!.ToString());
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        using var input = new MemoryStream(Convert.FromBase64String(query["SAMLRequest"].ToString()));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var document = XDocument.Load(deflate);
        var id = (string?)document.Root!.Attribute("ID");
        Assert.NotNull(id);
        var pending = await LoadPendingRequestAsync(id, ct);
        Assert.NotNull(pending);
        Assert.Equal(providerId, pending.LoginProviderId);
        return id;
    }

    private async Task AssertValidRetryAsync(HttpClient http, X509Certificate2 cert, string requestId,
        Guid userId, CancellationToken ct, Saml2AuthnResponseSignTypes signType = Saml2AuthnResponseSignTypes.SignAssertion)
    {
        var response = await PostToAcsAsync(http, MintSignedResponse(cert, requestId, signType: signType), ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.True(HasApplicationCookie(response));
        Assert.True((await LoadPendingRequestAsync(requestId, ct))!.IsConsumed);
        await AssertSignedInAsync(http, userId, ct);
    }

    private static bool HasApplicationCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(value => value.StartsWith("Modgud.Auth=", StringComparison.Ordinal)
            && !value.StartsWith("Modgud.Auth=;", StringComparison.Ordinal));

    private async Task AssertSignedInAsync(HttpClient http, Guid userId, CancellationToken ct)
    {
        var me = await http.GetAsync("/api/account/me", ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
        var user = body.RootElement;
        Assert.Equal(new ShortGuid(userId).ToString(), user.GetProperty("Id").GetString());
        Assert.Equal("samluser", user.GetProperty("UserName").GetString());
        Assert.Equal(SubjectNameId, user.GetProperty("Email").GetString());
        Assert.True(user.GetProperty("IsFederated").GetBoolean());
        Assert.False(user.GetProperty("IsFederatedMfa").GetBoolean());
        Assert.Equal("E2E SAML", user.GetProperty("IdpDisplayName").GetString());
    }

    private async Task<SamlPendingAuthnRequest?> LoadPendingRequestAsync(string id, CancellationToken ct)
    {
        await using var qs = GetTenantedSession();
        return await qs.LoadAsync<SamlPendingAuthnRequest>(id, ct);
    }

    private async Task<HttpResponseMessage> PostToAcsAsync(
        HttpClient http, string samlResponseBase64, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("SAMLResponse", samlResponseBase64),
            new KeyValuePair<string, string>("RelayState", "/"),
        });
        return await http.PostAsync($"/saml/{_slug}/acs", form, ct);
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

    private string MintSignedResponse(X509Certificate2 idpCert, string inResponseTo, bool additionalAttributes = false,
        Saml2AuthnResponseSignTypes signType = Saml2AuthnResponseSignTypes.SignAssertion,
        Action<Saml2AuthnResponse>? mutateBeforeSigning = null)
    {
        var idpConfig = new Saml2Configuration
        {
            Issuer = IdpEntityId,
            SigningCertificate = idpCert,
            // The SP's default floor is WantAssertionsSigned=true, which requires a
            // per-assertion ds:Signature. ITfoxtec signs the Response wrapper by
            // default, so ask it to sign the assertion instead.
            AuthnResponseSignType = signType,
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
        if (additionalAttributes)
        {
            // A bounded compatibility case: add attributes BEFORE signing so a
            // rejection cannot be dismissed as our having broken the signature.
            for (var i = 0; i < 16; i++)
                response.ClaimsIdentity.AddClaim(new Claim($"urn:test:attribute:{i}", $"Grüße <&> {i}"));
        }

        // appliesToAddress = the SP entity id => assertion audience. ITfoxtec signs
        // the assertion during Bind using the config's SigningCertificate, which is
        // what the SP's WantAssertionsSigned floor (default on) requires.
        response.CreateSecurityToken(SpEntityId);
        mutateBeforeSigning?.Invoke(response);

        var binding = new Saml2PostBinding { RelayState = "/" };
        binding.Bind(response);
        var encoded = ExtractSamlResponseField(binding.PostContent);
        if (additionalAttributes)
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var attributes = document.Descendants().Where(element => element.Name.LocalName == "Attribute").ToArray();
            for (var i = 0; i < 16; i++)
            {
                var attribute = Assert.Single(attributes, element => element.Attribute("Name")?.Value == $"urn:test:attribute:{i}");
                Assert.True(attribute.Elements().Any(element => element.Name.LocalName == "AttributeValue"
                    && element.Value == $"Grüße <&> {i}"));
            }
        }
        return encoded;
    }

    private static string ExtractSamlResponseField(string postContent)
    {
        var match = Regex.Match(postContent,
            "name=\"SAMLResponse\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "could not find SAMLResponse field in the bound POST content");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
