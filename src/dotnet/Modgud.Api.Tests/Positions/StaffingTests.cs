using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
/// MG-FT-05 — the staffing ceremony (plan §12/§13): an enrolled terminal
/// begins a WebAuthn ceremony with its enrollment token, a person with an
/// ACTIVE activation grant taps their passkey, and the custom staffing grant
/// opens a <see cref="StaffingSession"/> whose token subject is the POSITION.
/// At most one active session per terminal — a second tap supersedes the
/// first (ReplacedByNewActivation) and cuts its tokens off. Everything off
/// the happy path is refused: wrong device key, suspended grant, replayed
/// ceremony, flag off.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class StaffingTests : IntegrationTestBase
{
    public StaffingTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string DeviceEndpoint = "http://localhost/connect/device";
    private const string TokenEndpoint = "http://localhost/connect/token";
    private const string BeginEndpoint = "http://localhost/connect/staffing/begin";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task A_passkey_tap_opens_a_staffing_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-staff", ct);

        // Begin — the terminal asks for assertion options with its enrollment
        // token + a proof of its enrolled key.
        var begin = await BeginStaffingAsync(setup, ct);
        Assert.Single(begin.Options.GetProperty("allowCredentials").EnumerateArray());
        Assert.Equal(RpId, begin.Options.GetProperty("rpId").GetString());

        // Tap + redeem.
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");
        var tokenResp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResp.IsSuccessStatusCode, $"staffing grant failed ({(int)tokenResp.StatusCode}): {tokenBody}");
        using var tokens = JsonDocument.Parse(tokenBody);
        Assert.Equal("DPoP", tokens.RootElement.GetProperty("token_type").GetString());
        Assert.False(string.IsNullOrEmpty(tokens.RootElement.GetProperty("refresh_token").GetString()));

        // Session + terminal pointer + audit metadata.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = (await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
        Assert.Equal(StaffingSessionStatus.Active, staffing.Status);
        Assert.Equal(setup.UserId, staffing.ActivatedByUserId);
        Assert.Equal(setup.PositionId, staffing.PositionPrincipalId);
        Assert.Equal(setup.DeviceKey.Jkt, staffing.DpopJkt);
        Assert.False(string.IsNullOrEmpty(staffing.OAuthAuthorizationId));
        Assert.True(staffing.AbsoluteExpiresAt > DateTimeOffset.UtcNow.AddHours(15));

        var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(staffing.Id, terminal!.ActiveStaffingSessionId);

        // Event-sourced: session stream = started; terminal stream gained the
        // activation event (created + enrolled + activated).
        Assert.Single(await session.Events.FetchStreamAsync(staffing.Id, token: ct));
        Assert.Equal(3, (await session.Events.FetchStreamAsync(setup.TerminalId, token: ct)).Count);

        // The ceremony is single-use — a replay of the same ceremony_id fails.
        var replay = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_second_tap_supersedes_the_active_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-supersede", ct);

        var firstTokens = await TapAsync(setup, ct, signCount: 1);
        var firstRefresh = firstTokens.RootElement.GetProperty("refresh_token").GetString()!;
        firstTokens.Dispose();

        // The verifier advances the credential's signature counter — the
        // second tap must present a higher count or it fails closed.
        var secondTokens = await TapAsync(setup, ct, signCount: 2);
        secondTokens.Dispose();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var sessions = (await session.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct))
                .OrderBy(s => s.StartedAt).ToList();
            Assert.Equal(2, sessions.Count);
            Assert.Equal(StaffingSessionStatus.Ended, sessions[0].Status);
            Assert.Equal(StaffingSessionEndReason.ReplacedByNewActivation, sessions[0].EndReason);
            Assert.Equal(StaffingSessionStatus.Active, sessions[1].Status);

            var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
            Assert.Equal(sessions[1].Id, terminal!.ActiveStaffingSessionId);
        }

        // The replaced session's authorization is revoked — its refresh chain
        // is dead, not just idle.
        var refreshResp = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = firstRefresh,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(refreshResp.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_exchange_refuses_a_wrong_device_key()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-wrongtap", ct);

        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        using var attackerKey = new DpopProofBuilder();
        var resp = await PostTokenAsync(StaffingForm(setup.ClientId, begin.CeremonyId, assertion), attackerKey);
        Assert.False(resp.IsSuccessStatusCode);
        Assert.Contains("enrolled key", await resp.Content.ReadAsStringAsync(ct));

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        Assert.Empty(await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
    }

    [Fact]
    public async Task A_suspended_grant_cannot_open_a_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-suspended", ct);

        // Ceremony begun while the grant was still active…
        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        // …then the grant is suspended before the tap is redeemed.
        var suspend = await Client.PostAsync(
            $"/api/position/{new ShortGuid(setup.PositionId)}/grants/{setup.GrantId}/suspend", null, ct);
        Assert.True(suspend.IsSuccessStatusCode, await suspend.Content.ReadAsStringAsync(ct));

        var resp = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.False(resp.IsSuccessStatusCode);
        Assert.Contains("not authorized to staff", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Begin_requires_the_flag_and_an_enrollment_token()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-begin-gate", ct);

        // Anonymous → 401 (the OpenIddict validation scheme guards the route).
        var anon = Factory.CreateClient();
        var noToken = await anon.PostAsync("/connect/staffing/begin", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Missing DPoP proof → refused even with a valid enrollment token.
        var proofless = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        proofless.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        var prooflessResp = await Factory.CreateClient().SendAsync(proofless, ct);
        Assert.Equal(HttpStatusCode.Forbidden, prooflessResp.StatusCode);
        Assert.Contains("DPoP", await prooflessResp.Content.ReadAsStringAsync(ct));

        // Flag off → the surface does not exist.
        SetFeatureFlag(false);
        try
        {
            var dark = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
            dark.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
            dark.Headers.Add(DpopConstants.HeaderName,
                setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow));
            var darkResp = await Factory.CreateClient().SendAsync(dark, ct);
            Assert.Equal(HttpStatusCode.NotFound, darkResp.StatusCode);
        }
        finally
        {
            SetFeatureFlag(true);
        }
    }

    // ─── MG-FT-06: the staffing refresh (§14) ─────────────────────────────

    [Fact]
    public async Task The_staffing_chain_refreshes_without_a_new_tap()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-refresh", ct);

        var tokens = await TapAsync(setup, ct);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        tokens.Dispose();

        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        var body = await refresh.Content.ReadAsStringAsync(ct);
        Assert.True(refresh.IsSuccessStatusCode, $"staffing refresh failed ({(int)refresh.StatusCode}): {body}");
        using var refreshed = JsonDocument.Parse(body);
        Assert.Equal("DPoP", refreshed.RootElement.GetProperty("token_type").GetString());
        Assert.False(string.IsNullOrEmpty(refreshed.RootElement.GetProperty("refresh_token").GetString()));

        // No new session, no new ceremony — the shift just continues.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = (await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
        Assert.Equal(StaffingSessionStatus.Active, staffing.Status);
        var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(staffing.Id, terminal!.ActiveStaffingSessionId);
    }

    [Fact]
    public async Task An_expired_session_refresh_demands_a_new_tap()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-expired", ct);

        var tokens = await TapAsync(setup, ct);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        tokens.Dispose();

        // Push the session past its absolute end (the doc is projection-owned;
        // for the test a direct store is fine — the Ended event the refusal
        // appends keeps the mutated document).
        Guid sessionId;
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var staffing = (await docs.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
            sessionId = staffing.Id;
            staffing.AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            docs.Store(staffing);
            await docs.SaveChangesAsync(ct);
        }

        // §14.5 — the consumer must lock and demand a fresh tap.
        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.False(refresh.IsSuccessStatusCode);
        var body = await refresh.Content.ReadAsStringAsync(ct);
        Assert.Contains("staffing_required", body);
        Assert.Contains("interaction_required", body);

        // Lazy expiry ended the session for real: Ended(Expired), pointer
        // cleared, authorization dead (a second refresh attempt can't even
        // authenticate).
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = await session.LoadAsync<StaffingSession>(sessionId, ct);
            Assert.Equal(StaffingSessionStatus.Ended, staffing!.Status);
            Assert.Equal(StaffingSessionEndReason.Expired, staffing.EndReason);
            var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
            Assert.Null(terminal!.ActiveStaffingSessionId);
        }
        var retry = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.False(retry.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_revoked_grant_stops_the_refresh_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-degrant", ct);

        var tokens = await TapAsync(setup, ct);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        tokens.Dispose();

        // Suspend the grant WITHOUT the HTTP endpoint: the admin surface now
        // cascades the session end (MG-FT-07, its own test), which would kill
        // the refresh token before the §14.3 grant check ever runs. Appending
        // the event directly leaves the session alive — the state a racing
        // suspend produces — so the refresh-time check is what refuses.
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            docs.Events.Append(new ShortGuid(setup.GrantId).Guid, new PositionGrantSuspended(
                new ShortGuid(setup.GrantId).Guid, Guid.NewGuid(), DateTimeOffset.UtcNow));
            await docs.SaveChangesAsync(ct);
        }

        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.False(refresh.IsSuccessStatusCode);
        Assert.Contains("staffing_required", await refresh.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_wrong_key_cannot_refresh_the_staffing_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-refkey", ct);

        var tokens = await TapAsync(setup, ct);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;
        tokens.Dispose();

        using var attackerKey = new DpopProofBuilder();
        var refresh = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), attackerKey);
        Assert.False(refresh.IsSuccessStatusCode);

        // The chain itself stays alive for the legitimate key.
        var legit = await PostTokenAsync(RefreshForm(setup.ClientId, refreshToken), setup.DeviceKey);
        Assert.True(legit.IsSuccessStatusCode, await legit.Content.ReadAsStringAsync(ct));
    }

    private static Dictionary<string, string> RefreshForm(string clientId, string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["client_id"] = clientId,
    };

    // ─── MG-FT-07: locks + revocation cascades (§15) ──────────────────────

    [Fact]
    public async Task A_local_lock_ends_the_active_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-lock", ct);

        var tokens = await TapAsync(setup, ct);
        var staffingAccessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        tokens.Dispose();

        var lockResp = await PostLockAsync(setup.TerminalId, staffingAccessToken, setup.DeviceKey);
        var lockBody = await lockResp.Content.ReadAsStringAsync(ct);
        Assert.True(lockResp.IsSuccessStatusCode, $"lock failed ({(int)lockResp.StatusCode}): {lockBody}");
        Assert.Equal(1, JsonDocument.Parse(lockBody).RootElement.GetProperty("Ended").GetInt32());

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.LocalLock, ct);
    }

    [Fact]
    public async Task The_enrollment_token_locks_too_but_a_wrong_key_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-lock-enroll", ct);
        (await TapAsync(setup, ct)).Dispose();

        // Wrong device key → refused, session survives.
        using var attackerKey = new DpopProofBuilder();
        var attack = await PostLockAsync(setup.TerminalId, setup.EnrollmentAccessToken, attackerKey);
        Assert.Equal(HttpStatusCode.Forbidden, attack.StatusCode);

        // The enrollment token + enrolled key lock even with the staffing
        // access token expired/lost (§15.2 rationale).
        var lockResp = await PostLockAsync(setup.TerminalId, setup.EnrollmentAccessToken, setup.DeviceKey);
        Assert.True(lockResp.IsSuccessStatusCode, await lockResp.Content.ReadAsStringAsync(ct));
        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.LocalLock, ct);
    }

    [Fact]
    public async Task An_admin_force_lock_ends_the_session_idempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-forcelock", ct);
        (await TapAsync(setup, ct)).Dispose();

        Guid sessionId;
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            sessionId = (await session.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single().Id;
        }

        var force = await Client.PostAsync($"/api/staffing-session/{new ShortGuid(sessionId)}/force-lock", null, ct);
        var forceBody = await force.Content.ReadAsStringAsync(ct);
        Assert.True(force.IsSuccessStatusCode, $"force-lock failed ({(int)force.StatusCode}): {forceBody}");
        Assert.Equal(1, JsonDocument.Parse(forceBody).RootElement.GetProperty("Ended").GetInt32());
        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.RemoteLock, ct);

        // Idempotent: both admin lock shapes are successful no-ops now.
        var again = await Client.PostAsync($"/api/staffing-session/{new ShortGuid(sessionId)}/force-lock", null, ct);
        Assert.Equal(0, JsonDocument.Parse(await again.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("Ended").GetInt32());
        var terminalLock = await Client.PostAsync($"/api/position-terminal/{new ShortGuid(setup.TerminalId)}/force-lock", null, ct);
        Assert.Equal(0, JsonDocument.Parse(await terminalLock.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("Ended").GetInt32());
    }

    [Fact]
    public async Task A_zero_role_user_cannot_force_lock_or_list_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Lock", acronym: "ZL", email: "zerolock@test.com", password: "TestPass1234");
        var zero = await CreateAuthenticatedClientAsync("zl", "TestPass1234");
        var anyId = new ShortGuid(Guid.NewGuid()).ToString();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await zero.PostAsync($"/api/staffing-session/{anyId}/force-lock", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await zero.PostAsync($"/api/position-terminal/{anyId}/force-lock", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await zero.GetAsync($"/api/position/{anyId}/staffing-sessions", ct)).StatusCode);
    }

    [Fact]
    public async Task Revoking_the_grant_ends_the_running_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-cascade-grant", ct);
        (await TapAsync(setup, ct)).Dispose();

        var revoke = await Client.PostAsync(
            $"/api/position/{new ShortGuid(setup.PositionId)}/grants/{setup.GrantId}/revoke", null, ct);
        Assert.True(revoke.IsSuccessStatusCode, await revoke.Content.ReadAsStringAsync(ct));

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.GrantRevoked, ct);
    }

    [Fact]
    public async Task Disabling_the_terminal_ends_the_running_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-cascade-term", ct);
        (await TapAsync(setup, ct)).Dispose();

        var disable = await Client.PostAsync(
            $"/api/position/{new ShortGuid(setup.PositionId)}/terminals/{new ShortGuid(setup.TerminalId)}/disable", null, ct);
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync(ct));

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.TerminalDisabled, ct);
    }

    [Fact]
    public async Task Deactivating_the_position_ends_the_running_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-cascade-fn", ct);
        (await TapAsync(setup, ct)).Dispose();

        var update = await Client.PutAsJsonAsync($"/api/position/{new ShortGuid(setup.PositionId)}",
            new { IsActive = false }, JsonOptions, ct);
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync(ct));

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.PositionDisabled, ct);
    }

    [Fact]
    public async Task Binning_the_user_ends_their_session()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-cascade-user", ct);
        (await TapAsync(setup, ct)).Dispose();

        var delete = await Client.DeleteAsync($"/api/user/{new ShortGuid(setup.UserId)}", ct);
        Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync(ct));

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.UserDisabled, ct);
    }

    [Fact]
    public async Task The_janitor_ends_expired_sessions_and_prunes_lapsed_ceremonies()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-janitor", ct);
        (await TapAsync(setup, ct)).Dispose();

        // A lapsed ceremony + a session past its absolute end.
        var begin = await BeginStaffingAsync(setup, ct);
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var ceremony = await docs.LoadAsync<StaffingCeremony>(Guid.Parse(begin.CeremonyId), ct);
            ceremony!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            docs.Store(ceremony);
            var staffing = (await docs.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct)).Single();
            staffing.AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            docs.Store(staffing);
            await docs.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var revoker = scope.ServiceProvider.GetRequiredService<Modgud.Infrastructure.PositionTerminals.IStaffingRevoker>();
            var ended = await Modgud.Api.Features.Admin.Jobs.StaffingSweepJob.SweepAsync(docs, revoker, ct);
            Assert.Equal(1, ended);
        }

        await AssertSessionEndedAsync(setup.TerminalId, StaffingSessionEndReason.Expired, ct);
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            Assert.Null(await session.LoadAsync<StaffingCeremony>(Guid.Parse(begin.CeremonyId), ct));
        }
    }

    private async Task<HttpResponseMessage> PostLockAsync(Guid terminalId, string accessToken, DpopProofBuilder key)
    {
        var url = $"/connect/staffing/{terminalId}/lock";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add(DpopConstants.HeaderName,
            key.CreateProof("POST", $"http://localhost{url}", DateTimeOffset.UtcNow));
        return await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task AssertSessionEndedAsync(Guid terminalId, StaffingSessionEndReason expectedReason, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = (await session.Query<StaffingSession>()
            .Where(s => s.TerminalEnrollmentId == terminalId).ToListAsync(ct)).Single();
        Assert.Equal(StaffingSessionStatus.Ended, staffing.Status);
        Assert.Equal(expectedReason, staffing.EndReason);
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        Assert.Null(terminal!.ActiveStaffingSessionId);
    }

    // ─── scenario setup ───────────────────────────────────────────────────

    private sealed record StaffingSetup(
        Guid PositionId,
        Guid TerminalId,
        string ClientId,
        Guid UserId,
        string GrantId,
        string EnrollmentAccessToken,
        DpopProofBuilder DeviceKey,
        SoftwareWebAuthnAuthenticator Authenticator);

    /// <summary>Position (policy on) + granted user with a seeded RP-ID
    /// passkey + terminal slot enrolled via the full MG-FT-04 device flow.</summary>
    private async Task<StaffingSetup> SetUpEnrolledTerminalWithGrantedUserAsync(string accountName, CancellationToken ct)
    {
        // Position + terminal slot via the admin API.
        var fnResp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = new { Enabled = true },
        }, JsonOptions, ct);
        Assert.True(fnResp.IsSuccessStatusCode, await fnResp.Content.ReadAsStringAsync(ct));
        var fnId = new ShortGuid((await fnResp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id).Guid;

        var termResp = await Client.PostAsJsonAsync($"/api/position/{new ShortGuid(fnId)}/terminals",
            new { DisplayName = "Staff-Terminal", Location = "Tor 1", WebAuthnRpId = RpId }, JsonOptions, ct);
        Assert.True(termResp.IsSuccessStatusCode, await termResp.Content.ReadAsStringAsync(ct));
        var terminal = (await termResp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
        var terminalId = new ShortGuid(terminal.Id).Guid;

        // Granted user with a software passkey under the terminal's RP-ID.
        var userResp = await Client.PostAsJsonAsync("/api/user", new UserCreateDto
        {
            Firstname = "Staff",
            Lastname = accountName.ToUpperInvariant(),
            Acronym = $"S{Math.Abs(accountName.GetHashCode()) % 1000}",
            Email = $"{accountName}@staff.test",
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
                DisplayName = "Staffing test passkey",
                RpId = RpId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(ct);
        }

        // Enroll the terminal via the MG-FT-04 device flow.
        var deviceKey = new DpopProofBuilder();
        var (deviceCode, userCode) = await RequestDeviceCodeAsync(
            terminal.ClientId, deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow));
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode);
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

    // ─── flow helpers ─────────────────────────────────────────────────────

    private sealed record BeginResult(string CeremonyId, JsonElement Options);

    private async Task<BeginResult> BeginStaffingAsync(StaffingSetup setup, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        request.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow));
        var resp = await Factory.CreateClient().SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing begin failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return new BeginResult(
            doc.RootElement.GetProperty("ceremonyId").GetString()!,
            doc.RootElement.GetProperty("publicKey").Clone());
    }

    private Task<HttpResponseMessage> RedeemStaffingAsync(StaffingSetup setup, string ceremonyId, string assertion) =>
        PostTokenAsync(StaffingForm(setup.ClientId, ceremonyId, assertion), setup.DeviceKey);

    private static Dictionary<string, string> StaffingForm(string clientId, string ceremonyId, string assertion) => new()
    {
        ["grant_type"] = PositionGrantTypes.StaffingSession,
        ["client_id"] = clientId,
        ["ceremony_id"] = ceremonyId,
        ["assertion"] = assertion,
    };

    /// <summary>Begin + tap + redeem in one step; returns the parsed token response.</summary>
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

    private async Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form, DpopProofBuilder key)
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

    private async Task OpenVerificationAsync(HttpClient cookieClient, string userCode)
    {
        var resp = await cookieClient.GetAsync(
            $"/connect/verify?user_code={Uri.EscapeDataString(userCode)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
    }

    private Task<HttpResponseMessage> SubmitDecisionAsync(HttpClient cookieClient, string userCode) =>
        cookieClient.PostAsync("/connect/verify", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("user_code", userCode),
            new("decision", "approve"),
        }), TestContext.Current.CancellationToken);

    // Minimal ES256 DPoP proof factory — same shape as the enrollment tests'.
    internal sealed class DpopProofBuilder : IDisposable
    {
        private static readonly JsonSerializerOptions ProofJson =
            new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Jkt { get; }

        public DpopProofBuilder()
        {
            var p = _ec.ExportParameters(false);
            Jkt = JwkThumbprint.ForEc("P-256", p.Q.X!, p.Q.Y!);
        }

        public string CreateProof(string htm, string htu, DateTimeOffset iat)
        {
            var p = _ec.ExportParameters(false);
            var jwk = new { kty = "EC", crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!) };
            var header = new { typ = "dpop+jwt", alg = "ES256", jwk };
            var payload = new Dictionary<string, object>
            {
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["htm"] = htm,
                ["htu"] = htu,
                ["iat"] = iat.ToUnixTimeSeconds(),
            };
            var signingInput = $"{Seg(header)}.{Seg(payload)}";
            var sig = _ec.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{signingInput}.{B64(sig)}";
        }

        private static string Seg(object o) => B64(JsonSerializer.SerializeToUtf8Bytes(o, ProofJson));
        private static string B64(byte[] b) => Base64Url.EncodeToString(b);

        public void Dispose() => _ec.Dispose();
    }
}
