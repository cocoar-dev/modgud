using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// B6-SAML-03 — RefreshMetadataAsync read the cache entry, awaited the metadata fetch and
/// then wrote its updated copy back unconditionally. A provider disabled, deleted or
/// re-registered while the fetch ran was resurrected with its old configuration.
/// </summary>
public partial class DynamicSamlSchemeManagerTests
{
    public class RefreshRace
    {
        [Fact]
        public async Task A_refresh_overlapping_an_unregister_does_not_bring_the_provider_back()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var http = new GatedMetadataHttpClientFactory(BuildMetadataXml());
            var manager = NewManager(http);
            var provider = NewSamlProvider(flavorData: JsonDocument.Parse("""{"metadataUrl":"https://idp.test/metadata"}"""));
            await manager.RegisterAsync(provider);
            Assert.True(manager.TryGet(Realm, provider.Id, out _));

            http.HoldNext();
            var refresh = manager.RefreshMetadataAsync(Realm, provider.Id, TestContext.Current.CancellationToken);
            await http.Held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await manager.UnregisterAsync(provider.Id); // disabled / deleted meanwhile
            http.Release();
            await refresh;

            Assert.False(manager.TryGet(Realm, provider.Id, out _));
        }
    }

    private static DynamicSamlSchemeManager NewManager(IHttpClientFactory http) =>
        new(
            new SamlFlavorRegistry(new ISamlFlavor[] { new GenericSamlFlavor(), new EntraIdSamlFlavor(), new AdfsSamlFlavor() }),
            new SamlMetadataFetcher(http, NullLogger<SamlMetadataFetcher>.Instance),
            TimeProvider.System,
            new NoOpSecurityAuditLog(),
            NullLogger<DynamicSamlSchemeManager>.Instance);

    private static string BuildMetadataXml()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=refresh-race-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        return new XDocument(new XElement(md + "EntityDescriptor",
            new XAttribute("entityID", "urn:test:refresh-race"),
            new XElement(md + "IDPSSODescriptor",
                new XAttribute("protocolSupportEnumeration", "urn:oasis:names:tc:SAML:2.0:protocol"),
                new XElement(md + "KeyDescriptor", new XAttribute("use", "signing"),
                    new XElement(ds + "KeyInfo", new XElement(ds + "X509Data",
                        new XElement(ds + "X509Certificate", Convert.ToBase64String(cert.Export(X509ContentType.Cert)))))),
                new XElement(md + "SingleSignOnService",
                    new XAttribute("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"),
                    new XAttribute("Location", "https://idp.test/sso"))))).ToString();
    }

    /// <summary>Serves the metadata; after <see cref="HoldNext"/> the next request waits for
    /// <see cref="Release"/>.</summary>
    private sealed class GatedMetadataHttpClientFactory(string metadataXml) : IHttpClientFactory
    {
        private TaskCompletionSource? _gate;
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Held => _held.Task;
        public void HoldNext() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate?.TrySetResult();

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(GatedMetadataHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (owner._gate is { } gate)
                {
                    owner._held.TrySetResult();
                    await gate.Task.WaitAsync(cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(owner.MetadataXml) };
            }
        }

        private string MetadataXml => metadataXml;
    }
}
