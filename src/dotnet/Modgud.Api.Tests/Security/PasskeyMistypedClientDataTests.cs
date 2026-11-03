using System.Net;
using System.Net.Http.Json;
using System.Text;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B8-P4 — a clientDataJSON that is valid JSON but of the wrong shape (an <c>origin</c>
/// that is not a string, a root that is not an object) made the origin helper throw an
/// <see cref="InvalidOperationException"/>; the callers only catch <c>JsonException</c>,
/// so the hosted passkey login answered 500 instead of failing closed with 401.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PasskeyMistypedClientDataTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{\"type\":\"webauthn.get\",\"challenge\":\"abc\",\"origin\":5}")]
    [InlineData("{\"type\":\"webauthn.get\",\"challenge\":\"abc\",\"origin\":{\"host\":\"x\"}}")]
    [InlineData("[\"https://evil.example\"]")]
    [InlineData("\"https://evil.example\"")]
    public async Task Web_passkey_login_with_mistyped_client_data_fails_closed(string clientData)
    {
        var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        var options = await browser.PostAsJsonAsync("/api/account/passkey/login-options", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);

        var login = await browser.PostAsJsonAsync("/api/account/passkey/login", new
        {
            id = "AQIDBA",
            rawId = "AQIDBA",
            type = "public-key",
            response = new
            {
                authenticatorData = "AAAA",
                signature = "AAAA",
                clientDataJSON = Base64Url(clientData),
            },
        }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
