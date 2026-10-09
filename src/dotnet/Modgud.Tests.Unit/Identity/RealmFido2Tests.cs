using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Identity;

/// <summary>
/// Pins the per-client WebAuthn RP-ID / origin behaviour in <see cref="RealmFido2"/>.
/// The bug: a per-client RP-ID is meant to be a registrable SUFFIX of the app origin
/// (RP-ID <c>acmelist.example</c> for a page on <c>app.acmelist.example</c>), but the accepted
/// origin was derived as <c>https://{rpId}</c>, so a passkey enroll/login from the
/// real subdomain origin failed the FIDO2 origin allow-list. The fix accepts any
/// signed origin that is the RP-ID host or a subdomain of it — exactly the set
/// WebAuthn already scopes to that RP-ID — and nothing else.
/// </summary>
public class RealmFido2Tests
{
    // ── IsOriginUnderRpId — the security-critical suffix filter ──────────────────

    [Theory]
    // Same host and genuine subdomains are accepted.
    [InlineData("https://acmelist.example", "acmelist.example", false, true)]
    [InlineData("https://app.acmelist.example", "acmelist.example", false, true)]
    [InlineData("https://deep.app.acmelist.example", "acmelist.example", false, true)]
    // Look-alikes and foreign hosts are rejected.
    [InlineData("https://evil.at", "acmelist.example", false, false)]
    [InlineData("https://acmelist.example.evil.com", "acmelist.example", false, false)]
    [InlineData("https://evilacmelist.example", "acmelist.example", false, false)]
    // Scheme: https only in prod; http only when insecure (dev) is allowed.
    [InlineData("http://app.acmelist.example", "acmelist.example", false, false)]
    [InlineData("http://app.acmelist.example", "acmelist.example", true, true)]
    // Junk / empty / non-absolute is rejected.
    [InlineData("app.acmelist.example", "acmelist.example", false, false)]
    [InlineData("", "acmelist.example", false, false)]
    [InlineData("https://app.acmelist.example", "", false, false)]
    public void IsOriginUnderRpId_AcceptsOnlyRpIdHostOrSubdomain(
        string origin, string rpId, bool allowInsecure, bool expected)
        => Assert.Equal(expected, RealmFido2.IsOriginUnderRpId(origin, rpId, allowInsecure));

    // ── TryGetClientDataOrigin ───────────────────────────────────────────────────

    [Fact]
    public void TryGetClientDataOrigin_ReadsOrigin()
    {
        var clientData = Encoding.UTF8.GetBytes(
            "{\"type\":\"webauthn.create\",\"challenge\":\"abc\",\"origin\":\"https://app.acmelist.example\"}");
        Assert.Equal("https://app.acmelist.example", RealmFido2.TryGetClientDataOrigin(clientData));
    }

    [Fact]
    public void TryGetClientDataOrigin_NullOrGarbage_ReturnsNull()
    {
        Assert.Null(RealmFido2.TryGetClientDataOrigin(null));
        Assert.Null(RealmFido2.TryGetClientDataOrigin([]));
        Assert.Null(RealmFido2.TryGetClientDataOrigin(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(RealmFido2.TryGetClientDataOrigin(Encoding.UTF8.GetBytes("{\"type\":\"webauthn.create\"}")));
    }

    // ── IsOriginForRequest — hosted web ceremonies stay same-origin ──────────────

    [Theory]
    [InlineData("http://acmelist.auth-dev.localhost:4310", "http", "acmelist.auth-dev.localhost:4310", true)]
    [InlineData("https://acmelist.example.com", "https", "acmelist.example.com", true)]
    [InlineData("http://other.auth-dev.localhost:4310", "http", "acmelist.auth-dev.localhost:4310", false)]
    [InlineData("http://acmelist.auth-dev.localhost:4300", "http", "acmelist.auth-dev.localhost:4310", false)]
    [InlineData("https://acmelist.example.com/path", "https", "acmelist.example.com", false)]
    public void IsOriginForRequest_RequiresExactSchemeHostAndPort(
        string origin, string scheme, string host, bool expected)
        => Assert.Equal(expected, RealmFido2.IsOriginForRequest(origin, scheme, new(host)));

    // ── BuildConfiguration — the end of the wiring ───────────────────────────────

    [Fact]
    public void BuildConfiguration_PerClientRpId_AcceptsSignedSubdomainOrigin()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "acmelist.example",
            additionalOrigins: ["https://app.acmelist.example"]);

        Assert.Equal("acmelist.example", config.ServerDomain);          // RP-ID unchanged
        Assert.Contains("https://acmelist.example", config.Origins);    // the RP-ID host itself
        Assert.Contains("https://app.acmelist.example", config.Origins); // the real signed origin
    }

    [Fact]
    public void BuildConfiguration_ForeignSignedOrigin_NotAccepted()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "acmelist.example",
            additionalOrigins: ["https://evil.at"]);

        Assert.DoesNotContain("https://evil.at", config.Origins);
        Assert.Contains("https://acmelist.example", config.Origins);
    }

    [Fact]
    public void BuildConfiguration_RelatedOrigin_IsAcceptedOutsideTheRpId_OnlyWhenPassedAsRelated()
    {
        // WebAuthn related origin requests: the Modgud login page is not under the app's
        // RP ID; the app's /.well-known/webauthn lists it and the browser has checked that.
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var related = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "app.example-app.test",
            relatedOrigins: ["https://auth.cocoar.dev"]);
        var plain = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "app.example-app.test",
            additionalOrigins: ["https://auth.cocoar.dev"]);

        Assert.Equal("app.example-app.test", related.ServerDomain);
        Assert.Contains("https://auth.cocoar.dev", related.Origins);
        Assert.DoesNotContain("https://auth.cocoar.dev", plain.Origins);
    }

    [Fact]
    public void BuildConfiguration_RelatedOrigin_MustBeHttpsOutsideDevelopment()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "app.example-app.test",
            relatedOrigins: ["http://auth.cocoar.dev", "not a url"]);

        Assert.DoesNotContain("http://auth.cocoar.dev", config.Origins);
        Assert.DoesNotContain("not a url", config.Origins);
    }

    // ── Android app origins ──────────────────────────────────────────────────────

    private const string PlayOrigin = "android:apk-key-hash:NxKiGexuyBGDGdVtp4Is8MhaKBquHQNHiw-IG3hPtiw";

    [Theory]
    [InlineData(PlayOrigin)]                                            // the origin itself
    [InlineData("NxKiGexuyBGDGdVtp4Is8MhaKBquHQNHiw-IG3hPtiw")]         // the bare hash
    [InlineData("NxKiGexuyBGDGdVtp4Is8MhaKBquHQNHiw+IG3hPtiw=")]        // standard base64, padded
    [InlineData("37:12:A2:19:EC:6E:C8:11:83:19:D5:6D:A7:82:2C:F0:C8:5A:28:1A:AE:1D:03:47:8B:0F:88:1B:78:4F:B6:2C")] // Play Console fingerprint
    [InlineData("  3712a219ec6ec8118319d56da7822cf0c85a281aae1d03478b0f881b784fb62c ")] // hex, lower case
    public void NormalizeAndroidAppOrigin_AcceptsTheFormsAnAdminPastes(string raw)
        => Assert.Equal(PlayOrigin, RealmFido2.NormalizeAndroidAppOrigin(raw));

    [Theory]
    [InlineData("")]
    [InlineData("https://amzettel.at")]
    [InlineData("android:apk-key-hash:tooShort")]
    [InlineData("37:12:A2:19")]                                          // a truncated fingerprint
    [InlineData("SHA1:37:12:A2:19:EC:6E:C8:11:83:19:D5:6D:A7:82:2C:F0:C8:5A:28:1A")]
    public void NormalizeAndroidAppOrigin_RejectsEverythingElse(string raw)
        => Assert.Null(RealmFido2.NormalizeAndroidAppOrigin(raw));

    [Fact]
    public void BuildConfiguration_AppOrigins_AcceptOnlyAndroidAppOrigins()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "amzettel.at",
            appOrigins: [PlayOrigin, "https://evil.at", "android:apk-key-hash:short"]);

        Assert.Contains(PlayOrigin, config.Origins);
        Assert.DoesNotContain("https://evil.at", config.Origins);
        Assert.DoesNotContain("android:apk-key-hash:short", config.Origins);
    }

    [Fact]
    public void BuildConfiguration_AndroidOrigin_IsNeverAcceptedAsASignedOrRelatedOrigin()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "amzettel.at",
            additionalOrigins: [PlayOrigin], relatedOrigins: [PlayOrigin]);

        Assert.DoesNotContain(PlayOrigin, config.Origins);
    }

    private static readonly IWebHostEnvironment ProdEnv = new FakeWebHostEnvironment("Production");

    private sealed class FakeWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Modgud.Tests.Unit";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
