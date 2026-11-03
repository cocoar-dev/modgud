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
            // Consumed by a version-checked marker, not deleted (B8-P1); reaped by expiry.
            var consumed = Assert.Single(await session.Query<PasskeyEnrollCeremony>().Where(c => c.UserId == DefaultUser.Id).ToListAsync(ct));
            Assert.NotNull(consumed.ConsumedAt);
        }

        // Single use: the ceremony was consumed and the cookie deleted.
        var replay = await Client.PostAsync("/api/account/passkey/register",
            new StringContent(attestation, Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Register_with_an_attestation_for_another_origin_is_a_400_not_a_500()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        var (challenge, rpId) = await RegisterOptionsAsync();

        var response = await RegisterAsync(authenticator.CreateAttestationJson(challenge, rpId, "https://evil.example"));

        await AssertRefusedAsync(response, authenticator.CredentialId, expectedForUser: 0);
    }

    [Fact]
    public async Task Register_with_a_credential_id_already_stored_is_a_400_not_a_500()
    {
        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        // Held by another account, so this user still has no factor of its own and
        // register-options needs no fresh second factor; the FIDO2 uniqueness callback
        // looks at every credential of the realm.
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new StoredPasskeyCredential
            {
                Id = Guid.CreateVersion7(),
                UserId = Guid.NewGuid(),
                CredentialId = authenticator.CredentialId,
                PublicKey = authenticator.CosePublicKey(),
                UserHandle = [8, 8],
                DisplayName = "Passkey",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var (challenge, rpId) = await RegisterOptionsAsync();

        var response = await RegisterAsync(authenticator.CreateAttestationJson(challenge, rpId, $"https://{rpId}"));

        await AssertRefusedAsync(response, authenticator.CredentialId, expectedForUser: 0);
        await using var session = GetTenantedSession();
        Assert.Single(await session.Query<StoredPasskeyCredential>().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_with_a_malformed_attestation_body_is_a_400_not_a_500()
    {
        await RegisterOptionsAsync();

        var response = await RegisterAsync("""{"id":"x","rawId":"x","type":"public-key","response":"not-an-object"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(string Challenge, string RpId)> RegisterOptionsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client.PostAsync("/api/account/passkey/register-options", content: null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"register-options failed ({(int)response.StatusCode})");
        using var options = JsonDocument.Parse(body);
        return (options.RootElement.GetProperty("challenge").GetString()!,
            options.RootElement.GetProperty("rp").GetProperty("id").GetString()!);
    }

    private Task<HttpResponseMessage> RegisterAsync(string attestation)
        => Client.PostAsync("/api/account/passkey/register",
            new StringContent(attestation, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private async Task AssertRefusedAsync(HttpResponseMessage response, byte[] credentialId, int expectedForUser)
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Passkey registration failed", await response.Content.ReadAsStringAsync(ct));
        await using var session = GetTenantedSession();
        var mine = await session.Query<StoredPasskeyCredential>().Where(c => c.UserId == DefaultUser!.Id).ToListAsync(ct);
        Assert.Equal(expectedForUser, mine.Count);
        Assert.DoesNotContain(mine, c => c.CredentialId.SequenceEqual(credentialId));
        // Consumed before verifying, so a refused attestation cannot be retried on it.
        var ceremony = Assert.Single(await session.Query<PasskeyEnrollCeremony>().Where(c => c.UserId == DefaultUser!.Id).ToListAsync(ct));
        Assert.NotNull(ceremony.ConsumedAt);
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
