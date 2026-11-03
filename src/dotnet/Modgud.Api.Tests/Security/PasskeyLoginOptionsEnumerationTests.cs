using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B8-P2 — the anonymous web passkey login options answered a submitted username with
/// that account's credential ids in <c>allowCredentials</c>, and with an empty list for an
/// unknown account: an account-existence oracle that also disclosed raw credential ids
/// before any authentication. Like the native begin, the anonymous options now always
/// carry an empty list (discoverable credentials); the username is ignored.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PasskeyLoginOptionsEnumerationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_options_do_not_reveal_whether_an_account_has_passkeys()
    {
        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(new StoredPasskeyCredential
            {
                Id = Guid.NewGuid(), UserId = DefaultUser!.Id, RpId = "localhost",
                CredentialId = [1, 2, 3, 4, 5, 6, 7, 8], PublicKey = [9, 9], UserHandle = [8, 8],
                DisplayName = "Enumeration probe", CreatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var known = await AllowCredentialsAsync("tu");
        var unknown = await AllowCredentialsAsync("nobody-here");
        var none = await AllowCredentialsAsync(null);

        Assert.Empty(known);
        Assert.Empty(unknown);
        Assert.Empty(none);
    }

    private async Task<IReadOnlyList<string>> AllowCredentialsAsync(string? userName)
    {
        using var browser = Factory.CreateDefaultClient(new CookieContainerHandler());
        browser.BaseAddress = new Uri("https://localhost");
        using var response = await browser.PostAsJsonAsync("/api/account/passkey/login-options",
            new { UserName = userName }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("localhost", options.GetProperty("rpId").GetString());
        return options.TryGetProperty("allowCredentials", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList()
            : [];
    }
}
