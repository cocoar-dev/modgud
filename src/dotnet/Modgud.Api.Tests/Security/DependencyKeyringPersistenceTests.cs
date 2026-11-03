using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.DataProtection;

namespace Modgud.Api.Tests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class DependencyKeyringPersistenceTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_provider_reads_Marten_keyring_and_cookie_ticket(bool wrapKeysWithCertificate)
    {
        // Two independent mini-containers, not a cached provider. This checks
        // the product XML repository and framework formats, not a browser login
        // or an operator's real certificate/old deployment keyring.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=modgud-keyring-patch-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var wrappingCertificate = wrapKeysWithCertificate ? certificate : null;
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        var repository = new MartenXmlRepository(store, "system");
        string cookie;
        string value;
        using (var writer = CreateProvider(repository, wrappingCertificate, readOnly: false))
        {
            // Existing unwrapped keys remain readable by design. Explicitly
            // activate a new key so the wrapped case cannot silently reuse one.
            var now = DateTimeOffset.UtcNow;
            writer.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
            var protection = writer.GetRequiredService<IDataProtectionProvider>();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "synthetic-keyring-user")], "Cookies"));
            cookie = CookieFormat(protection).Protect(new AuthenticationTicket(principal,
                new AuthenticationProperties(), "Cookies"));
            value = protection.CreateProtector("patch-persistence-value").Protect("synthetic-value");
        }

        Assert.NotEmpty(repository.GetAllElements());
        if (wrapKeysWithCertificate)
            Assert.True(repository.GetAllElements().Any(element =>
                element.Descendants(XNamespace.Get("http://schemas.asp.net/2015/03/dataProtection")
                    + "encryptedSecret").Any()), "The synthetic key must be wrapped at rest.");

        using var reader = CreateProvider(new MartenXmlRepository(store, "system"), wrappingCertificate, readOnly: true);
        var freshProtection = reader.GetRequiredService<IDataProtectionProvider>();
        var ticket = CookieFormat(freshProtection).Unprotect(cookie);
        Assert.NotNull(ticket);
        Assert.True(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == "synthetic-keyring-user");
        Assert.True(freshProtection.CreateProtector("patch-persistence-value").Unprotect(value) == "synthetic-value");
        Assert.Throws<CryptographicException>(() => freshProtection.CreateProtector("different-purpose").Unprotect(value));
        Assert.Null(CookieFormat(freshProtection).Unprotect("not-a-protected-ticket"));
        if (wrapKeysWithCertificate)
        {
            using var withoutCertificate = CreateProvider(new MartenXmlRepository(store, "system"),
                certificate: null, readOnly: true);
            Assert.Null(CookieFormat(withoutCertificate.GetRequiredService<IDataProtectionProvider>()).Unprotect(cookie));
        }
    }

    private static TicketDataFormat CookieFormat(IDataProtectionProvider provider) => new(provider.CreateProtector(
        "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Cookies", "v2"));

    private static ServiceProvider CreateProvider(MartenXmlRepository repository,
        X509Certificate2? certificate, bool readOnly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddDataProtection().SetApplicationName("Modgud-system")
            .AddKeyManagementOptions(options => options.XmlRepository = repository);
        if (certificate is not null)
            builder.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        if (readOnly) builder.DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider();
    }
}
