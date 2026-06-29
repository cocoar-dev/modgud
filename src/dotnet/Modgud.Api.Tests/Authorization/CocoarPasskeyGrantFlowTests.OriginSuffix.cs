using System.Text;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Regression for the per-client RP-ID origin mismatch: when the per-client
/// <c>WebAuthnRpId</c> is a registrable SUFFIX of the app origin (RP-ID
/// <c>a.localhost</c>, page on <c>sub.a.localhost</c>), enrollment used to fail with
/// 400 "Passkey enrollment failed." because the accepted origin was derived as
/// <c>https://{rpId}</c>. The fix accepts the signed origin when it is the RP-ID host
/// or a subdomain of it.
/// </summary>
public partial class CocoarPasskeyGrantFlowTests
{
    [Fact]
    public async Task NativeEnroll_SignedOriginIsSubdomainOfClientRpId_Succeeds()
    {
        await EnableNativeGrantsAsync();
        await SeedPasskeyClientAsync("app-a", rpId: "a.localhost");

        // Bootstrap a login → an app-a access token (existing seeded credential).
        using var bootstrap = new SoftwareWebAuthnAuthenticator(DefaultUser!.Id.ToByteArray());
        await SeedCredentialAsync(bootstrap.CredentialId, bootstrap.CosePublicKey(), bootstrap.UserHandle, rpId: "a.localhost");
        var (bootCeremony, bootChallenge, bootRpId) = await BeginAsync(clientId: "app-a");
        var bootAssertion = bootstrap.CreateAssertionJson(bootChallenge, bootRpId, $"https://{bootRpId}");
        var tokenResp = await PostPasskeyAsync("app-a", bootCeremony, bootAssertion);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResp.IsSuccessStatusCode, $"bootstrap login failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        var accessToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;

        var bearer = Factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        // Enroll begin → options pinned to the client's RP-ID a.localhost.
        var beginResp = await bearer.PostAsync("/connect/passkey/enroll/begin", content: null, TestContext.Current.CancellationToken);
        var beginBody = await beginResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(beginResp.IsSuccessStatusCode, $"enroll/begin failed ({(int)beginResp.StatusCode}): {beginBody}");
        using var beginJson = JsonDocument.Parse(beginBody);
        var enrollCeremonyId = beginJson.RootElement.GetProperty("ceremonyId").GetString()!;
        var opts = beginJson.RootElement.GetProperty("options");
        var enrollChallenge = opts.GetProperty("challenge").GetString()!;
        var enrollRpId = opts.GetProperty("rp").GetProperty("id").GetString()!;
        Assert.Equal("a.localhost", enrollRpId);

        // The credential is created on a SUBDOMAIN of the RP-ID — the case that broke.
        using var enrolling = new SoftwareWebAuthnAuthenticator(Encoding.UTF8.GetBytes(DefaultUser!.Id.ToString()));
        var origin = $"https://sub.{enrollRpId}"; // https://sub.a.localhost — a registrable suffix of a.localhost
        var attestation = enrolling.CreateAttestationJson(enrollChallenge, enrollRpId, origin);
        var enrollReqBody = $"{{\"ceremonyId\":\"{enrollCeremonyId}\",\"attestation\":{attestation}}}";

        var enrollResp = await bearer.PostAsync("/connect/passkey/enroll",
            new StringContent(enrollReqBody, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var enrollRespBody = await enrollResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(enrollResp.IsSuccessStatusCode,
            $"enroll from subdomain origin failed ({(int)enrollResp.StatusCode}): {enrollRespBody}");

        // Stored under the client's RP-ID (a.localhost), not the subdomain.
        var stored = await LoadCredentialAsync(enrolling.CredentialId);
        Assert.NotNull(stored);
        Assert.Equal("a.localhost", stored!.RpId);
    }
}
