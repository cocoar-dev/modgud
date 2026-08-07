using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Identity;

/// <summary>
/// Pins the per-client WebAuthn RP-ID / origin behaviour in <see cref="RealmFido2"/>.
/// The bug: a per-client RP-ID is meant to be a registrable SUFFIX of the app origin
/// (RP-ID <c>amzettel.at</c> for a page on <c>app.amzettel.at</c>), but the accepted
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
    [InlineData("https://amzettel.at", "amzettel.at", false, true)]
    [InlineData("https://app.amzettel.at", "amzettel.at", false, true)]
    [InlineData("https://deep.app.amzettel.at", "amzettel.at", false, true)]
    // Look-alikes and foreign hosts are rejected.
    [InlineData("https://evil.at", "amzettel.at", false, false)]
    [InlineData("https://amzettel.at.evil.com", "amzettel.at", false, false)]
    [InlineData("https://evilamzettel.at", "amzettel.at", false, false)]
    // Scheme: https only in prod; http only when insecure (dev) is allowed.
    [InlineData("http://app.amzettel.at", "amzettel.at", false, false)]
    [InlineData("http://app.amzettel.at", "amzettel.at", true, true)]
    // Junk / empty / non-absolute is rejected.
    [InlineData("app.amzettel.at", "amzettel.at", false, false)]
    [InlineData("", "amzettel.at", false, false)]
    [InlineData("https://app.amzettel.at", "", false, false)]
    public void IsOriginUnderRpId_AcceptsOnlyRpIdHostOrSubdomain(
        string origin, string rpId, bool allowInsecure, bool expected)
        => Assert.Equal(expected, RealmFido2.IsOriginUnderRpId(origin, rpId, allowInsecure));

    // ── TryGetClientDataOrigin ───────────────────────────────────────────────────

    [Fact]
    public void TryGetClientDataOrigin_ReadsOrigin()
    {
        var clientData = Encoding.UTF8.GetBytes(
            "{\"type\":\"webauthn.create\",\"challenge\":\"abc\",\"origin\":\"https://app.amzettel.at\"}");
        Assert.Equal("https://app.amzettel.at", RealmFido2.TryGetClientDataOrigin(clientData));
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
    [InlineData("http://amzettel.auth-dev.localhost:4310", "http", "amzettel.auth-dev.localhost:4310", true)]
    [InlineData("https://amzettel.example.com", "https", "amzettel.example.com", true)]
    [InlineData("http://other.auth-dev.localhost:4310", "http", "amzettel.auth-dev.localhost:4310", false)]
    [InlineData("http://amzettel.auth-dev.localhost:4300", "http", "amzettel.auth-dev.localhost:4310", false)]
    [InlineData("https://amzettel.example.com/path", "https", "amzettel.example.com", false)]
    public void IsOriginForRequest_RequiresExactSchemeHostAndPort(
        string origin, string scheme, string host, bool expected)
        => Assert.Equal(expected, RealmFido2.IsOriginForRequest(origin, scheme, new(host)));

    // ── BuildConfiguration — the end of the wiring ───────────────────────────────

    [Fact]
    public void BuildConfiguration_PerClientRpId_AcceptsSignedSubdomainOrigin()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "amzettel.at",
            additionalOrigins: ["https://app.amzettel.at"]);

        Assert.Equal("amzettel.at", config.ServerDomain);          // RP-ID unchanged
        Assert.Contains("https://amzettel.at", config.Origins);    // the RP-ID host itself
        Assert.Contains("https://app.amzettel.at", config.Origins); // the real signed origin
    }

    [Fact]
    public void BuildConfiguration_ForeignSignedOrigin_NotAccepted()
    {
        var realm = new Realm { Slug = "system", DisplayName = "Acme", PrimaryDomain = "auth.cocoar.dev" };

        var config = RealmFido2.BuildConfiguration(
            realm, ProdEnv, rpIdOverride: "amzettel.at",
            additionalOrigins: ["https://evil.at"]);

        Assert.DoesNotContain("https://evil.at", config.Origins);
        Assert.Contains("https://amzettel.at", config.Origins);
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
