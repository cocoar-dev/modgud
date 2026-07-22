using Modgud.Api.Features.Admin;

namespace Modgud.Tests.Unit.Api.Features.Admin;

public class AppSettingsEndpointsTests
{
    [Theory]
    [InlineData("/connect/authorize?client_id=web-client", "web-client")]
    [InlineData("/CONNECT/AUTHORIZE?scope=openid&client_id=my%20client", "my client")]
    public void ExtractAuthorizeClientId_AcceptsOnlyLocalAuthorizeContinuation(
        string returnUrl,
        string expected)
    {
        Assert.Equal(expected, AppSettingsEndpoints.ExtractAuthorizeClientId(returnUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://idp.example/connect/authorize?client_id=client")]
    [InlineData("//evil.example/connect/authorize?client_id=client")]
    [InlineData("/connect/authorize/extra?client_id=client")]
    [InlineData("/connect/token?client_id=client")]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/authorize?scope=openid")]
    [InlineData("/connect/authorize?client_id=")]
    [InlineData("/connect/authorize?client_id=one&client_id=two")]
    [InlineData("/connect/authorize\\?client_id=client")]
    [InlineData("/connect/authorize?client_id=client\r\nX-Test:true")]
    public void ExtractAuthorizeClientId_RejectsUntrustedOrAmbiguousInput(string? returnUrl)
    {
        Assert.Null(AppSettingsEndpoints.ExtractAuthorizeClientId(returnUrl));
    }

    [Fact]
    public void ExtractAuthorizeClientId_RejectsOversizedInput()
    {
        var returnUrl = "/connect/authorize?client_id=" + new string('a', 8192);

        Assert.Null(AppSettingsEndpoints.ExtractAuthorizeClientId(returnUrl));
    }
}
