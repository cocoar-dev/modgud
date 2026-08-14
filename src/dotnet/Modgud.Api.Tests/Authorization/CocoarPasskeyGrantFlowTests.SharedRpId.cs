using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// MG-FT-00 spike 3 — one passkey credential shared by two public clients that
/// carry the SAME per-client RP ID (the AlertHub function-terminal scenario:
/// terminal-left and terminal-right are distinct OAuth clients, but both live
/// under the one AlertHub RP ID the staff passkeys are enrolled for).
///
/// Pins the two facts MG-FT-05 builds on:
/// 1. The credential verifies for BOTH clients — the verifier's candidate
///    filter is RP-ID-scoped, not client-scoped, and the single
///    clone-detection counter advances across both.
/// 2. Client provenance still holds: a ceremony begun for one client cannot be
///    redeemed by the other, even with a genuine assertion — the ceremony
///    ClientId pin (not the RP-ID crypto) is what keeps token-authorization
///    provenance unambiguous when clients share an RP ID.
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    private const string SharedRpId = "alerthub.localhost";

    [Fact]
    public async Task PasskeyGrant_TwoClientsSharedRpId_SameCredential_BothMintTokens()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("terminal-left", rpId: SharedRpId);
        await SeedPasskeyClientAsync("terminal-right", rpId: SharedRpId);

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(
            authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle, rpId: SharedRpId);

        // Tap on the LEFT terminal.
        var (leftCeremony, leftChallenge, leftRpId) = await BeginAsync(clientId: "terminal-left");
        Assert.Equal(SharedRpId, leftRpId);
        var leftAssertion = authenticator.CreateAssertionJson(leftChallenge, leftRpId, $"https://{leftRpId}", signCount: 1);
        var leftResponse = await PostPasskeyAsync("terminal-left", leftCeremony, leftAssertion);
        var leftBody = await leftResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(leftResponse.IsSuccessStatusCode, $"left terminal login failed ({(int)leftResponse.StatusCode}): {leftBody}");

        // Tap on the RIGHT terminal with the SAME credential (§13.5: the same
        // passkey must be able to succeed on terminal left and right).
        var (rightCeremony, rightChallenge, rightRpId) = await BeginAsync(clientId: "terminal-right");
        Assert.Equal(SharedRpId, rightRpId);
        var rightAssertion = authenticator.CreateAssertionJson(rightChallenge, rightRpId, $"https://{rightRpId}", signCount: 2);
        var rightResponse = await PostPasskeyAsync("terminal-right", rightCeremony, rightAssertion);
        var rightBody = await rightResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(rightResponse.IsSuccessStatusCode, $"right terminal login failed ({(int)rightResponse.StatusCode}): {rightBody}");

        // Both minted for the same user...
        foreach (var body in new[] { leftBody, rightBody })
        {
            using var json = JsonDocument.Parse(body);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("access_token").GetString()!);
            Assert.Equal(DefaultUser!.Id.ToString(), jwt.Subject);
        }

        // ...and against ONE clone-detection line: both verifies advanced the
        // same stored counter (the shared verifier persists it per credential,
        // not per client).
        var credential = await LoadCredentialAsync(authenticator.CredentialId);
        Assert.NotNull(credential);
        Assert.Equal(2u, credential!.SignatureCount);
    }

    [Fact]
    public async Task PasskeyGrant_SharedRpId_CeremonyBeganForOtherClient_InvalidGrant()
    {
        // With a shared RP ID the FIDO2 rpIdHash check can no longer tell the
        // two clients apart — the ceremony ClientId pin is the only provenance
        // boundary left. A genuine assertion for a ceremony begun by the LEFT
        // terminal must not mint tokens for the RIGHT one.
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("terminal-left", rpId: SharedRpId);
        await SeedPasskeyClientAsync("terminal-right", rpId: SharedRpId);

        using var authenticator = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(
            authenticator.CredentialId, authenticator.CosePublicKey(), authenticator.UserHandle, rpId: SharedRpId);

        var (ceremonyId, challenge, rpId) = await BeginAsync(clientId: "terminal-left");
        var assertion = authenticator.CreateAssertionJson(challenge, rpId, $"https://{rpId}");

        var response = await PostPasskeyAsync("terminal-right", ceremonyId, assertion);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_grant", body);

        // The pin rejected BEFORE the verify: the credential was never touched.
        var credential = await LoadCredentialAsync(authenticator.CredentialId);
        Assert.Equal(0u, credential!.SignatureCount);

        // And the ceremony burned on the attempt — the left terminal cannot be
        // replayed onto it either (single-use consumption is client-agnostic).
        Assert.False(await CeremonyIsRedeemableAsync(Guid.Parse(ceremonyId)));
    }
}
