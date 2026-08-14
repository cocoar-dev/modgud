using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// MG-FT-10 — the concurrency rows of the test matrix (plan §13.5 + work-item
/// MG-FT-10) that no earlier package pinned: racing taps, double-redeeming a
/// ceremony, and a refresh racing a lock. The invariants under test are the
/// terminal stream's activation lock (FetchForWriting version guard), the
/// ceremony's optimistic-concurrency consume, and authorization-anchored
/// revocation (a revoked authorization kills rotated refresh tokens too).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class StaffingConcurrencyTests : IntegrationTestBase
{
    public StaffingConcurrencyTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string DeviceEndpoint = "http://localhost/connect/device";
    private const string TokenEndpoint = "http://localhost/connect/token";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task Two_parallel_taps_leave_exactly_one_active_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-race-tap", ct);

        // Two independent ceremonies, redeemed AT THE SAME TIME. Outcomes the
        // invariant allows: both succeed sequentially-enough (the second
        // supersedes the first), or the loser of the terminal-version race
        // gets its retryable invalid_grant, or the shared signature counter
        // fails one verify closed. NEVER two active sessions.
        var begin1 = await BeginStaffingAsync(setup, ct);
        var begin2 = await BeginStaffingAsync(setup, ct);
        var assertion1 = setup.Authenticator.CreateAssertionJson(
            begin1.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount: 1);
        var assertion2 = setup.Authenticator.CreateAssertionJson(
            begin2.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount: 2);

        var responses = await Task.WhenAll(
            RedeemStaffingAsync(setup, begin1.CeremonyId, assertion1),
            RedeemStaffingAsync(setup, begin2.CeremonyId, assertion2));

        Assert.Contains(responses, r => r.IsSuccessStatusCode);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var sessions = await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct);
        var active = sessions.Where(s => s.Status == StaffingSessionStatus.Active).ToList();
        var survivor = Assert.Single(active);

        // The terminal points at exactly the surviving session, and any other
        // session ended as replaced — nothing dangles.
        var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(survivor.Id, terminal!.ActiveStaffingSessionId);
        Assert.All(sessions.Where(s => s.Id != survivor.Id),
            s => Assert.Equal(StaffingSessionEndReason.ReplacedByNewActivation, s.EndReason));
    }

    [Fact]
    public async Task A_ceremony_double_redeem_wins_at_most_once()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-race-cer", ct);

        // ONE ceremony, the SAME assertion, redeemed twice in parallel: the
        // version-checked ConsumedAt store lets at most one continue past the
        // consume — a captured ceremony_id is worthless.
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        var responses = await Task.WhenAll(
            RedeemStaffingAsync(setup, begin.CeremonyId, assertion),
            RedeemStaffingAsync(setup, begin.CeremonyId, assertion));

        Assert.True(responses.Count(r => r.IsSuccessStatusCode) <= 1,
            "a single ceremony must never mint two token sets");

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var sessions = await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct);
        Assert.True(sessions.Count <= 1, "a single ceremony must never open two sessions");
    }

    [Fact]
    public async Task A_refresh_racing_a_force_lock_cannot_keep_the_chain_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-race-lock", ct);

        var tokens = await TapAsync(setup, ct);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        tokens.Dispose();

        Guid sessionId;
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            sessionId = (await session.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single().Id;
        }

        // Fire the refresh and the admin force-lock at the same time. The
        // refresh may win the race and return 200 with a rotated token — but
        // both live on the SAME authorization, which the lock revokes, so the
        // chain is dead either way.
        var refreshTask = PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        var lockTask = Client.PostAsync($"/api/staffing-session/{new ShortGuid(sessionId)}/force-lock", null, ct);
        await Task.WhenAll(refreshTask, lockTask);
        Assert.True(lockTask.Result.IsSuccessStatusCode, await lockTask.Result.Content.ReadAsStringAsync(ct));

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = await session.LoadAsync<StaffingSession>(sessionId, ct);
            Assert.Equal(StaffingSessionStatus.Ended, staffing!.Status);
        }

        // Whichever refresh token is newest (rotated or original), a
        // POST-lock refresh must fail — the authorization is revoked.
        var latestRefresh = refreshToken;
        if (refreshTask.Result.IsSuccessStatusCode)
        {
            using var rotated = JsonDocument.Parse(await refreshTask.Result.Content.ReadAsStringAsync(ct));
            latestRefresh = rotated.RootElement.GetProperty("refresh_token").GetString()!;
        }
        var afterLock = await PostTokenAsync(RefreshForm(setup.ClientId, latestRefresh), setup.DeviceKey);
        Assert.False(afterLock.IsSuccessStatusCode, "the staffing chain must be dead after the lock");
    }

    // ─── scenario setup + flow helpers (shared shape with StaffingTests) ──

    private sealed record StaffingSetup(
        Guid PositionId,
        Guid TerminalId,
        string ClientId,
        Guid UserId,
        string GrantId,
        string EnrollmentAccessToken,
        StaffingTests.DpopProofBuilder DeviceKey,
        SoftwareWebAuthnAuthenticator Authenticator);

    private async Task<StaffingSetup> SetUpEnrolledTerminalWithGrantedUserAsync(string accountName, CancellationToken ct)
    {
        var fnResp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = new { Enabled = true },
        }, JsonOptions, ct);
        Assert.True(fnResp.IsSuccessStatusCode, await fnResp.Content.ReadAsStringAsync(ct));
        var fnId = new ShortGuid((await fnResp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id).Guid;

        var termResp = await Client.PostAsJsonAsync($"/api/position/{new ShortGuid(fnId)}/terminals",
            new { DisplayName = "Race-Terminal", Location = "Tor 9", WebAuthnRpId = RpId }, JsonOptions, ct);
        Assert.True(termResp.IsSuccessStatusCode, await termResp.Content.ReadAsStringAsync(ct));
        var terminal = (await termResp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
        var terminalId = new ShortGuid(terminal.Id).Guid;

        var userResp = await Client.PostAsJsonAsync("/api/user", new UserCreateDto
        {
            Firstname = "Race",
            Lastname = accountName.ToUpperInvariant(),
            Acronym = $"R{Math.Abs(accountName.GetHashCode()) % 1000}",
            Email = $"{accountName}@race.test",
            IsActive = true,
        }, JsonOptions, ct);
        Assert.True(userResp.IsSuccessStatusCode, await userResp.Content.ReadAsStringAsync(ct));
        var userId = new ShortGuid((await userResp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct))!.Id!).Guid;

        var grantResp = await Client.PostAsJsonAsync($"/api/position/{new ShortGuid(fnId)}/grants",
            new { UserId = new ShortGuid(userId).ToString() }, JsonOptions, ct);
        Assert.True(grantResp.IsSuccessStatusCode, await grantResp.Content.ReadAsStringAsync(ct));
        var grantId = (await grantResp.Content.ReadFromJsonAsync<PositionGrantDto>(JsonOptions, ct))!.Id;

        var authenticator = new SoftwareWebAuthnAuthenticator(userId.ToByteArray());
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new StoredPasskeyCredential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CredentialId = authenticator.CredentialId,
                PublicKey = authenticator.CosePublicKey(),
                UserHandle = authenticator.UserHandle,
                SignatureCount = 0,
                AttestationType = "none",
                DisplayName = "Race test passkey",
                RpId = RpId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(ct);
        }

        var deviceKey = new StaffingTests.DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        var verify = await admin.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}", ct);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, verify.StatusCode);
        var approve = await admin.PostAsync("/connect/verify", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("user_code", userCode),
            new("decision", "approve"),
        }), ct);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");
        var poll = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = DeviceCodeGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = terminal.ClientId,
        }, deviceKey);
        var pollBody = await poll.Content.ReadAsStringAsync(ct);
        Assert.True(poll.IsSuccessStatusCode, $"enrollment poll failed ({(int)poll.StatusCode}): {pollBody}");
        using var tokens = JsonDocument.Parse(pollBody);
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;

        return new StaffingSetup(fnId, terminalId, terminal.ClientId, userId, grantId,
            accessToken, deviceKey, authenticator);
    }

    private sealed record BeginResult(string CeremonyId, JsonElement Options);

    private async Task<BeginResult> BeginStaffingAsync(StaffingSetup setup, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        request.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", "http://localhost/connect/staffing/begin", DateTimeOffset.UtcNow));
        var resp = await Factory.CreateClient().SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing begin failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return new BeginResult(
            doc.RootElement.GetProperty("ceremonyId").GetString()!,
            doc.RootElement.GetProperty("publicKey").Clone());
    }

    private Task<HttpResponseMessage> RedeemStaffingAsync(StaffingSetup setup, string ceremonyId, string assertion) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = PositionGrantTypes.StaffingSession,
            ["client_id"] = setup.ClientId,
            ["ceremony_id"] = ceremonyId,
            ["assertion"] = assertion,
        }, setup.DeviceKey);

    private async Task<JsonDocument> TapAsync(StaffingSetup setup, CancellationToken ct, uint signCount = 1)
    {
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount);
        var resp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing grant failed ({(int)resp.StatusCode}): {body}");
        return JsonDocument.Parse(body);
    }

    private static Dictionary<string, string> RefreshForm(string clientId, string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["client_id"] = clientId,
    };

    private async Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form, StaffingTests.DpopProofBuilder key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add(DpopConstants.HeaderName, key.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        return await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(string clientId, string dpopProof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
            }),
        };
        request.Headers.Add(DpopConstants.HeaderName, dpopProof);
        var resp = await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"/connect/device failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("user_code").GetString()!);
    }
}
