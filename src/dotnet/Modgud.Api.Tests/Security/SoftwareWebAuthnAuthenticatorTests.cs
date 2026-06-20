using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins that <see cref="SoftwareWebAuthnAuthenticator"/> produces an assertion the
/// REAL Fido2NetLib (4.0.1) accepts — in-memory, no DB, no container. If this
/// fails, the assertion construction (COSE key, authenticatorData, ES256 DER
/// signature, clientDataJSON) is wrong; the integration crypto tests (which run
/// the same assertion through the urn:cocoar:passkey grant) depend on it.
/// </summary>
public class SoftwareWebAuthnAuthenticatorTests
{
    [Fact]
    public async Task ProducesAssertion_RealFido2_Accepts()
    {
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Test",
            Origins = new HashSet<string> { "https://localhost" },
        });

        var userHandle = Guid.NewGuid().ToByteArray();
        using var auth = new SoftwareWebAuthnAuthenticator(userHandle);

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        var challengeB64 = SoftwareWebAuthnAuthenticator.B64Url(options.Challenge);
        var assertionJson = auth.CreateAssertionJson(challengeB64, "localhost", "https://localhost");
        var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
            assertionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertion,
            OriginalOptions = options,
            StoredPublicKey = auth.CosePublicKey(),
            StoredSignatureCounter = 0,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                Task.FromResult(args.UserHandle.SequenceEqual(userHandle)),
        }, TestContext.Current.CancellationToken);

        // SignCount advanced to the value the authenticator reported (1).
        Assert.Equal(1u, result.SignCount);
    }
}
