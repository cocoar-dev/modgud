using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using System.Xml.Linq;
using MessagePack;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>
/// Small library/format compatibility checks, not CVE payloads or evidence that
/// Modgud exposes the synthetic MessagePack/CMS inputs used here.
/// </summary>
public sealed class DependencyPatchCompatibilityTests
{
    [Fact]
    public void Restored_patch_assemblies_are_actually_loaded()
    {
        // At least the patched version: a newer shared framework on the machine
        // (CI installs the latest 10.0.x runtime) supplies a newer assembly and wins.
        var versions = LoadedVersions();
        AssertAtLeast(versions["MessagePack"], "2.5.303");
        AssertAtLeast(versions["Pkcs"], "10.0.13");
        AssertAtLeast(versions["Xml"], "10.0.13");
        AssertAtLeast(versions["SignalR.Common"], "10.0.13");
    }

    private static void AssertAtLeast(string loaded, string patched) =>
        Assert.True(Version.Parse(loaded.Split('-')[0]) >= Version.Parse(patched),
            $"loaded {loaded}, patched version is {patched}");

    [Fact]
    public void MessagePack_small_object_graph_roundtrips_with_untrusted_data_security()
    {
        var options = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
        object[] value = ["Grüße <&>", 42, true, new object[] { "nested", 7 }];
        var encoded = MessagePackSerializer.Serialize<object>(value, options);
        var decoded = Assert.IsType<object[]>(MessagePackSerializer.Deserialize<object>(encoded, options));
        Assert.Equal("Grüße <&>", decoded[0]);
        Assert.Equal(42, Convert.ToInt32(decoded[1]));
        Assert.Equal(true, decoded[2]);
        Assert.Equal("nested", Assert.IsType<object[]>(decoded[3])[0]);
    }

    [Fact]
    public void MessagePack_rejects_a_short_invalid_input()
    {
        var options = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
        Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackSerializer.Deserialize<object>(new byte[] { 0xc1 }, options));
    }

    [Fact]
    public void Cms_signature_roundtrips_and_changed_content_is_refused()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=modgud-patch-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var signed = new SignedCms(new ContentInfo("synthetic patch check"u8.ToArray()), detached: true);
        signed.ComputeSignature(new CmsSigner(certificate));
        var encoded = signed.Encode();
        var decoded = new SignedCms(new ContentInfo("synthetic patch check"u8.ToArray()), detached: true);
        decoded.Decode(encoded);
        decoded.CheckSignature(verifySignatureOnly: true); // self-signed fixture, not chain trust

        var changed = new SignedCms(new ContentInfo("changed patch content"u8.ToArray()), detached: true);
        changed.Decode(encoded);
        Assert.Throws<CryptographicException>(() => changed.CheckSignature(verifySignatureOnly: true));
    }

    [Fact]
    public void MimeKit_unicode_alternatives_roundtrip_without_a_network_transport()
    {
        // Mirrors the SMTP service's MimeKit construction, not an executed SMTP send.
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Modgud Test", "sender@example.test"));
        message.To.Add(new MailboxAddress("Test User", "recipient@example.test"));
        message.ReplyTo.Add(MailboxAddress.Parse("reply@example.test"));
        message.Subject = "Übersicht – Nachricht";
        message.Body = new BodyBuilder
        {
            TextBody = "Grüße & Hinweise",
            HtmlBody = "<p>Grüße &amp; Hinweise</p>",
        }.ToMessageBody();
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var parsed = MimeMessage.Load(stream);
        Assert.Equal(message.Subject, parsed.Subject);
        Assert.Equal("Grüße & Hinweise", parsed.TextBody);
        Assert.Equal("<p>Grüße &amp; Hinweise</p>", parsed.HtmlBody);
        Assert.Equal("sender@example.test", Assert.Single(parsed.From.Mailboxes).Address);
        Assert.Equal("recipient@example.test", Assert.Single(parsed.To.Mailboxes).Address);
        Assert.Equal("reply@example.test", Assert.Single(parsed.ReplyTo.Mailboxes).Address);
        Assert.Equal("alternative", parsed.Body.ContentType.MediaSubtype);
    }

    [Fact]
    public void Serialized_keyring_reads_cookie_and_protected_value_in_a_fresh_provider()
    {
        // Normal test runs stay entirely in memory. An explicit two-build probe
        // may save/read ONLY synthetic fixture material in an owner-private file
        // outside the repo. Never pass a real keyring or cookie to these options.
        var file = Environment.GetEnvironmentVariable("MODGUD_TEST_KEYRING_COMPAT_FILE");
        var mode = Environment.GetEnvironmentVariable("MODGUD_TEST_KEYRING_COMPAT_MODE");
        if (file is not null) ValidateSyntheticFixturePath(file);
        KeyringFixture fixture;
        if (file is not null && mode == "read")
        {
            fixture = JsonSerializer.Deserialize<KeyringFixture>(File.ReadAllText(file))!;
            Assert.True(fixture.Versions["MessagePack"].StartsWith("2.5.302", StringComparison.Ordinal));
            // The pre-patch build already loaded the installed shared-framework
            // XML patch, despite its older explicit package graph. Do not turn
            // this two-build probe into a claim of an older-runtime upgrade.
            Assert.True(fixture.Versions["Xml"].StartsWith("10.0.13", StringComparison.Ordinal));
            Assert.True(fixture.Versions["Pkcs"].StartsWith("10.0.13", StringComparison.Ordinal));
            Assert.True(fixture.Versions["SignalR.Common"].StartsWith("10.0.13", StringComparison.Ordinal));
            AssertAtLeast(LoadedVersions()["Xml"], "10.0.13");
        }
        else
        {
            Assert.True(file is null || mode == "write", "Invalid synthetic compatibility probe mode.");
            fixture = CreateKeyringFixture();
            if (file is not null)
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var output = new FileStream(file, options);
                JsonSerializer.Serialize(output, fixture);
            }
        }

        var repository = new MemoryXmlRepository(fixture.Keys.Select(XElement.Parse));
        using var reader = CreateProvider(repository, "Modgud-Patch-Test", readOnly: true);
        var protector = reader.GetRequiredService<IDataProtectionProvider>();
        var ticket = CookieFormat(protector).Unprotect(fixture.Cookie);
        Assert.NotNull(ticket);
        Assert.True(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == "synthetic-user");
        Assert.True(protector.CreateProtector("synthetic-value").Unprotect(fixture.ProtectedValue) == "synthetic-value");

        using var otherTenant = CreateProvider(repository, "Modgud-Other-Test", readOnly: true);
        Assert.Null(CookieFormat(otherTenant.GetRequiredService<IDataProtectionProvider>()).Unprotect(fixture.Cookie));
        Assert.Null(CookieFormat(protector).Unprotect("not-a-protected-ticket"));
    }

    private static KeyringFixture CreateKeyringFixture()
    {
        var repository = new MemoryXmlRepository([]);
        using var writer = CreateProvider(repository, "Modgud-Patch-Test", readOnly: false);
        var protector = writer.GetRequiredService<IDataProtectionProvider>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "synthetic-user")], "Cookies"));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), "Cookies");
        var cookie = CookieFormat(protector).Protect(ticket);
        var value = protector.CreateProtector("synthetic-value").Protect("synthetic-value");
        return new(repository.GetAllElements().Select(element => element.ToString(SaveOptions.DisableFormatting)).ToArray(),
            cookie, value, LoadedVersions());
    }

    private static TicketDataFormat CookieFormat(IDataProtectionProvider provider) => new(provider.CreateProtector(
        "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Cookies", "v2"));

    private static ServiceProvider CreateProvider(MemoryXmlRepository repository, string applicationName, bool readOnly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddDataProtection().SetApplicationName(applicationName)
            .AddKeyManagementOptions(options => options.XmlRepository = repository);
        if (readOnly) builder.DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string> LoadedVersions() => new()
    {
        ["MessagePack"] = VersionOf(typeof(MessagePackSerializer)),
        ["Pkcs"] = VersionOf(typeof(SignedCms)),
        ["Xml"] = VersionOf(typeof(SignedXml)),
        ["SignalR.Common"] = VersionOf(typeof(Microsoft.AspNetCore.SignalR.Protocol.HubMessage)),
    };

    private static string VersionOf(Type type) => type.Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private static void ValidateSyntheticFixturePath(string file)
    {
        Assert.True(Path.IsPathFullyQualified(file)
            && Path.GetFileName(file).StartsWith("modgud-test-keyring-", StringComparison.Ordinal),
            "Use a dedicated absolute synthetic fixture path outside the repository.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, ".git"))
            && !Directory.Exists(Path.Combine(repository.FullName, ".git")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        Assert.False(Path.GetFullPath(file).StartsWith(repository.FullName + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Synthetic compatibility material must stay outside the repository.");
        if (File.Exists(file))
        {
            Assert.Null(new FileInfo(file).LinkTarget);
            if (!OperatingSystem.IsWindows())
                Assert.True((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute)) == 0, "Synthetic compatibility material must be owner-private.");
        }
    }

    private sealed record KeyringFixture(string[] Keys, string Cookie, string ProtectedValue,
        Dictionary<string, string> Versions);

    private sealed class MemoryXmlRepository(IEnumerable<XElement> elements) : IXmlRepository
    {
        private readonly List<XElement> _elements = elements.Select(element => new XElement(element)).ToList();
        public IReadOnlyCollection<XElement> GetAllElements() => _elements.Select(element => new XElement(element)).ToArray();
        public void StoreElement(XElement element, string? friendlyName) => _elements.Add(new XElement(element));
    }
}
