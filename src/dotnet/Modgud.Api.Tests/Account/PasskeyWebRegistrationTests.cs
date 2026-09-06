using System.Net;
using System.Text;
using System.Text.Json;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Account;

/// <summary>
/// The cookie-authenticated web passkey registration end to end, with a
/// software authenticator producing a real ES256 attestation. Since ADR 0022
/// the attestation options live in a server-side <see cref="PasskeyEnrollCeremony"/>
/// referenced by the <c>Modgud.Passkey.Enroll</c> cookie — the second request of
/// the ceremony may be served by any node — and the ASP.NET session is gone.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PasskeyWebRegistrationTests : IntegrationTestBase
{
    public PasskeyWebRegistrationTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RegisterOptions_then_Register_stores_the_credential_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());

        var optionsResp = await Client.PostAsync("/api/account/passkey/register-options", content: null, ct);
        var optionsBody = await optionsResp.Content.ReadAsStringAsync(ct);
        Assert.True(optionsResp.IsSuccessStatusCode, $"register-options failed ({(int)optionsResp.StatusCode}): {optionsBody}");
        Assert.Contains(optionsResp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Modgud.Passkey.Enroll=", StringComparison.Ordinal));
        Assert.DoesNotContain(optionsResp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("Modgud.Session=", StringComparison.Ordinal));

        using var options = JsonDocument.Parse(optionsBody);
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;
        var rpId = options.RootElement.GetProperty("rp").GetProperty("id").GetString()!;

        // The ceremony is a document in the realm database, not process memory.
        await using (var session = GetTenantedSession())
        {
            var pending = await session.Query<PasskeyEnrollCeremony>()
                .Where(c => c.UserId == DefaultUser.Id)
                .ToListAsync(ct);
            Assert.Single(pending);
            Assert.Equal(rpId, pending[0].RpId);
        }

        var attestation = authenticator.CreateAttestationJson(challenge, rpId, $"https://{rpId}");
        var registerResp = await Client.PostAsync("/api/account/passkey/register",
            new StringContent(attestation, Encoding.UTF8, "application/json"), ct);
        var registerBody = await registerResp.Content.ReadAsStringAsync(ct);
        Assert.True(registerResp.IsSuccessStatusCode, $"register failed ({(int)registerResp.StatusCode}): {registerBody}");

        await using (var session = GetTenantedSession())
        {
            var all = await session.Query<StoredPasskeyCredential>().ToListAsync(ct);
            Assert.Contains(all, c => c.CredentialId.SequenceEqual(authenticator.CredentialId) && c.UserId == DefaultUser.Id);
            Assert.Empty(await session.Query<PasskeyEnrollCeremony>().Where(c => c.UserId == DefaultUser.Id).ToListAsync(ct));
        }

        // Single use: the ceremony was consumed and the cookie deleted.
        var replay = await Client.PostAsync("/api/account/passkey/register",
            new StringContent(attestation, Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Register_without_options_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var fresh = await CreateAuthenticatedClientAsync("tu", DefaultPassword);

        var resp = await fresh.PostAsync("/api/account/passkey/register",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
