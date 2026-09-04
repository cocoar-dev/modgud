using Modgud.Application.Services;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>ADR 0009 — what a client may register as its back-channel logout URI.</summary>
public class BackChannelLogoutUriValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://rp.example.com/oidc/backchannel-logout")]
    [InlineData("https://rp.example.com:8443/logout?tenant=acme")]
    [InlineData("http://localhost:5000/logout")]
    [InlineData("http://127.0.0.1/logout")]
    [InlineData("http://[::1]:3000/logout")]
    public void Accepts_absolute_https_and_loopback_http(string? value) =>
        Assert.Null(OAuthAdminMapping.ValidateBackChannelLogoutUri(value));

    [Theory]
    [InlineData("http://rp.example.com/logout", "https")]
    [InlineData("ftp://rp.example.com/logout", "https")]
    [InlineData("https://rp.example.com/logout#fragment", "fragment")]
    [InlineData("/relative/logout", "absolute")]
    [InlineData("https://10.1.2.3/logout", "private")]
    [InlineData("https://192.168.1.10/logout", "private")]
    [InlineData("https://172.16.0.1/logout", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private")]
    [InlineData("https://100.64.0.1/logout", "private")]
    [InlineData("https://[fe80::1]/logout", "private")]
    [InlineData("https://[fd00::1]/logout", "private")]
    public void Rejects_plain_http_fragments_and_private_targets(string value, string expectedReason)
    {
        var error = OAuthAdminMapping.ValidateBackChannelLogoutUri(value);
        Assert.NotNull(error);
        Assert.Equal("OAuth.InvalidBackChannelLogoutUri", error!.Value.Code);
        Assert.Contains(expectedReason, error.Value.Description, StringComparison.OrdinalIgnoreCase);
    }
}
