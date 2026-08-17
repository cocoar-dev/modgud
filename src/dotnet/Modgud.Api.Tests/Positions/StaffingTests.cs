using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Api.Features.Positions;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Services;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Microsoft.AspNetCore.Identity;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Apis;
using Microsoft.Extensions.Options;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

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
        Assert.Equal("personal-passkey", staffing.Evidence.MethodId);
        Assert.Equal("dpop", staffing.Evidence.Binding);
        Assert.Equal(setup.UserId, staffing.Evidence.UserId);
        Assert.Equal(setup.DeviceKey.Jkt, staffing.DpopJkt);
        Assert.False(string.IsNullOrEmpty(staffing.OAuthAuthorizationId));
        Assert.True(staffing.AbsoluteExpiresAt > DateTimeOffset.UtcNow.AddHours(15));

        var terminal = await session.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(staffing.Id, terminal!.ActiveStaffingSessionId);

        // Event-sourced: session stream = started; terminal stream gained the
        // activation event (created + enrolled + activated).
        var staffingStream = await session.Events.FetchStreamAsync(staffing.Id, token: ct);
        var started = Assert.IsType<StaffingSessionStartedV2>(Assert.Single(staffingStream).Data);
        Assert.Equal(staffing.Evidence, started.Evidence);
        Assert.Equal(3, (await session.Events.FetchStreamAsync(setup.TerminalId, token: ct)).Count);

        // The ceremony is single-use — a replay of the same ceremony_id fails.
        var replay = await RedeemStaffingAsync(setup, begin.CeremonyId, assertion);
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_staffing_scope_creates_the_consumer_audience_and_position_resource_access()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        const string appSlug = "staffing-audience-app";
        const string audience = "https://alerthub.localhost/api";
        const string scopeName = "alerthub-terminal";
        var app = await CreateStaffingBusinessResourceAsync(appSlug, audience, scopeName, ct);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync(
            "fn-staff-audience", ct,
            businessScopes: [scopeName],
            appIds: [new ShortGuid(app.Id).ToString()]);

        var positionRole = await Factory.CreateTestRoleAsync(
            $"StaffingAlarmRead_{Guid.NewGuid():N}",
            [("alarm", "read")], appSlug: appSlug);
        await Factory.CreateTestGroupAsync(
            $"StaffingPosition_{Guid.NewGuid():N}",
            [setup.PositionId], [positionRole.Id], boundTo: [appSlug]);

        var begin = await BeginStaffingAsync(setup, ct);
        var assertion = setup.Authenticator.CreateAssertionJson(
            begin.Options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");
        var form = StaffingForm(setup.ClientId, begin.CeremonyId, assertion);
        form["scope"] = scopeName;
        form["resource"] = audience;
        var response = await PostTokenForBindingAsync(form, setup);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var tokens = JsonDocument.Parse(body);
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;

        using var serviceScope = Factory.Services.CreateScope();
        var manager = serviceScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await manager.FindByReferenceIdAsync(accessToken, ct);
        Assert.NotNull(token);
        var payload = await manager.GetPayloadAsync(token!, ct);
        Assert.False(string.IsNullOrWhiteSpace(payload));
        var jwt = new JsonWebToken(payload);
        Assert.Contains(audience, jwt.Audiences);

        var payloadJson = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(jwt.EncodedPayload));
        using var payloadDocument = JsonDocument.Parse(payloadJson);
        var resource = payloadDocument.RootElement
            .GetProperty("resource_access")
            .GetProperty(audience);
        Assert.Contains("alarm:read",
            resource.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));
        Assert.NotEmpty(resource.GetProperty("roles").EnumerateArray());

        // The terminal stays enrolled, but changing its audience profile must
        // kill the already-minted staffing chain immediately.
        var update = await Client.PutAsJsonAsync(
            $"/api/position-terminal/{new ShortGuid(setup.TerminalId)}/oauth-access",
            new { DisplayName = "No business audience", Scopes = Array.Empty<string>(), AppIds = Array.Empty<string>() },
            JsonOptions, ct);
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync(ct));

        using var verificationScope = Factory.Services.CreateScope();
        var query = verificationScope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = Assert.Single(await query.Query<StaffingSession>()
            .Where(item => item.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
        Assert.Equal(StaffingSessionStatus.Ended, staffing.Status);
        Assert.Equal(StaffingSessionEndReason.PolicyTightened, staffing.EndReason);
        var terminal = await query.LoadAsync<TerminalEnrollment>(setup.TerminalId, ct);
        Assert.Equal(TerminalEnrollmentStatus.Active, terminal!.Status);
        Assert.Null(terminal.ActiveStaffingSessionId);
    }

    [Theory]
    [InlineData(DeviceBindingIds.ClientSecret)]
    [InlineData(DeviceBindingIds.None)]
    public async Task Weaker_terminal_bindings_can_staff_and_refresh_without_dpop(string binding)
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync(
            $"fn-staff-{binding}", ct, binding);

        using var tokens = await TapAsync(setup, ct);
        Assert.Equal("Bearer", tokens.RootElement.GetProperty("token_type").GetString());
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        var refresh = await PostTokenForBindingAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken!,
            ["client_id"] = setup.ClientId,
        }, setup);
        var refreshBody = await refresh.Content.ReadAsStringAsync(ct);
        Assert.True(refresh.IsSuccessStatusCode,
            $"{binding} refresh failed ({(int)refresh.StatusCode}): {refreshBody}");
        using var refreshed = JsonDocument.Parse(refreshBody);
        Assert.Equal("Bearer", refreshed.RootElement.GetProperty("token_type").GetString());

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var staffing = Assert.Single(await session.Query<StaffingSession>()
            .Where(item => item.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
        Assert.Equal(binding, staffing.Evidence.Binding);
        Assert.Null(staffing.DpopJkt);
    }

    [Fact]
    public async Task Legacy_v1_control_chain_survives_f4_until_the_terminal_assignment_is_widened()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-legacy-control", ct);

        await RewriteAsLegacyControlTokenAsync(setup.EnrollmentAccessToken, setup.PositionId, ct);
        await RewriteAsLegacyControlTokenAsync(setup.EnrollmentRefreshToken, setup.PositionId, ct);

        // The pre-F4 chain remains usable while its original singleton
        // position assignment has not changed.
        var begin = await BeginStaffingAsync(setup, ct);
        Assert.Equal(RpId, begin.Options.GetProperty("rpId").GetString());
        var refresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = setup.EnrollmentRefreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        var refreshBody = await refresh.Content.ReadAsStringAsync(ct);
        Assert.True(refresh.IsSuccessStatusCode, refreshBody);
        using var refreshed = JsonDocument.Parse(refreshBody);
        var refreshedAccessToken = refreshed.RootElement.GetProperty("access_token").GetString()!;
        var refreshedRefreshToken = refreshed.RootElement.GetProperty("refresh_token").GetString()!;
        await AssertLegacyControlTokenAsync(refreshedAccessToken, setup.PositionId, setup.TerminalId, ct);

        var secondResponse = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = "fn-legacy-control-second",
            TerminalPolicy = new { Enabled = true },
        }, JsonOptions, ct);
        Assert.True(secondResponse.IsSuccessStatusCode, await secondResponse.Content.ReadAsStringAsync(ct));
        var secondPositionId = new ShortGuid(
            (await secondResponse.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id).Guid;

        // Public administration requires a fresh slot for this operation. The
        // event simulates an upgraded data set whose assignment has already
        // been widened, so both legacy-token entry points must still fail shut.
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            docs.Events.Append(setup.TerminalId, new TerminalAllowedPositionsChanged(
                setup.TerminalId, [setup.PositionId, secondPositionId], Guid.NewGuid(), DateTimeOffset.UtcNow));
            await docs.SaveChangesAsync(ct);
        }

        var rejectedBegin = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        rejectedBegin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedAccessToken);
        rejectedBegin.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", BeginEndpoint, DateTimeOffset.UtcNow, refreshedAccessToken));
        var rejectedBeginResponse = await Factory.CreateClient().SendAsync(rejectedBegin, ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedBeginResponse.StatusCode);
        Assert.Contains("Staffing.LegacyControlToken",
            await rejectedBeginResponse.Content.ReadAsStringAsync(ct));

        var rejectedRefresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshedRefreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(rejectedRefresh.IsSuccessStatusCode);
        Assert.Contains("requires re-enrollment",
            await rejectedRefresh.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Step_up_is_session_bound_action_bound_short_lived_and_cannot_widen_scopes()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-step-up", ct);

        using var staffingTokens = await TapAsync(setup, ct, signCount: 1);
        var staffingAccessToken = staffingTokens.RootElement.GetProperty("access_token").GetString()!;

        const string action = "alarm:acknowledge";
        const string nonce = "operation-123";
        var stepUpUrl = $"/connect/staffing/{setup.TerminalId}/step-up";
        var beginRequest = new HttpRequestMessage(HttpMethod.Post, stepUpUrl)
        {
            Content = JsonContent.Create(new
            {
                MethodId = ActivationProofMethodIds.PersonalPasskey,
                Action = action,
                Nonce = nonce,
            }),
        };
        beginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffingAccessToken);
        beginRequest.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", $"http://localhost{stepUpUrl}", DateTimeOffset.UtcNow, staffingAccessToken));
        var beginResponse = await Factory.CreateClient().SendAsync(beginRequest, ct);
        var beginBody = await beginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(beginResponse.IsSuccessStatusCode, beginBody);
        using var begin = JsonDocument.Parse(beginBody);
        var ceremonyId = begin.RootElement.GetProperty("ceremonyId").GetString()!;
        var publicKey = begin.RootElement.GetProperty("publicKey");
        var assertion = setup.Authenticator.CreateAssertionJson(
            publicKey.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount: 2);

        var form = StaffingForm(setup.ClientId, ceremonyId, assertion);
        form["step_up"] = "true";
        // Adversarial input: the exchange must ignore request scopes and use
        // the scope snapshot pinned from the current staffing session.
        form["scope"] = Scopes.OfflineAccess;
        var response = await PostTokenAsync(form, setup.DeviceKey);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, body);
        using var tokens = JsonDocument.Parse(body);
        Assert.Equal("DPoP", tokens.RootElement.GetProperty("token_type").GetString());
        Assert.False(tokens.RootElement.TryGetProperty("refresh_token", out _));

        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await manager.FindByReferenceIdAsync(accessToken, ct);
        Assert.NotNull(token);
        var payload = await manager.GetPayloadAsync(token!, ct);
        Assert.False(string.IsNullOrWhiteSpace(payload));
        var jwt = new JsonWebToken(payload);
        Assert.Equal(PositionTokenUses.StaffingStepUp,
            jwt.GetClaim(PositionTokenClaimTypes.TokenUse).Value);
        Assert.Equal(PositionAuthenticationContextReferences.StaffingStepUp,
            jwt.GetClaim(Claims.AuthenticationContextReference).Value);
        Assert.Equal(action, jwt.GetClaim(PositionTokenClaimTypes.StepUpAction).Value);
        Assert.Equal(nonce, jwt.GetClaim(PositionTokenClaimTypes.StepUpNonce).Value);
        Assert.Equal(setup.PositionId.ToString(), jwt.GetClaim(Claims.Subject).Value);
        Assert.Equal(setup.TerminalId.ToString(),
            jwt.GetClaim(PositionTokenClaimTypes.TerminalId).Value);
        Assert.Equal(ActivationProofMethodIds.PersonalPasskey,
            jwt.GetClaim(PositionTokenClaimTypes.ActivationProof).Value);
        Assert.DoesNotContain(jwt.Claims,
            claim => claim.Type == Claims.Scope && claim.Value.Contains(Scopes.OfflineAccess, StringComparison.Ordinal));
        var creationDate = await manager.GetCreationDateAsync(token!, ct);
        var expirationDate = await manager.GetExpirationDateAsync(token!, ct);
        Assert.NotNull(creationDate);
        Assert.NotNull(expirationDate);
        Assert.InRange(expirationDate!.Value - creationDate!.Value,
            TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Multi_position_candidates_are_disclosed_only_after_proof_and_selection_is_single_use()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-proof-first-a", ct);

        var secondResponse = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = "fn-proof-first-b",
            DisplayName = "Proof-only position B",
            TerminalPolicy = new { Enabled = true },
        }, JsonOptions, ct);
        Assert.True(secondResponse.IsSuccessStatusCode, await secondResponse.Content.ReadAsStringAsync(ct));
        var second = new ShortGuid(
            (await secondResponse.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id).Guid;
        var secondGrant = await Client.PostAsJsonAsync($"/api/position/{new ShortGuid(second)}/grants",
            new { UserId = new ShortGuid(setup.UserId).ToString() }, JsonOptions, ct);
        Assert.True(secondGrant.IsSuccessStatusCode, await secondGrant.Content.ReadAsStringAsync(ct));

        // This test shortcut projects the assignment as if both positions had
        // been selected before enrollment. The public API separately verifies
        // that an active slot cannot be widened without re-enrollment.
        using (var scope = Factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            docs.Events.Append(setup.TerminalId, new TerminalAllowedPositionsChanged(
                setup.TerminalId, [setup.PositionId, second], Guid.NewGuid(), DateTimeOffset.UtcNow));
            await docs.SaveChangesAsync(ct);
        }

        var bypassRequest = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin")
        {
            Content = JsonContent.Create(new { PositionId = new ShortGuid(second).ToString() }),
        };
        bypassRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        bypassRequest.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken));
        var bypass = await Factory.CreateClient().SendAsync(bypassRequest, ct);
        Assert.Equal(HttpStatusCode.Forbidden, bypass.StatusCode);
        Assert.Contains("Staffing.ProofRequiredBeforeSelection", await bypass.Content.ReadAsStringAsync(ct));

        var beginRequest = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        beginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        beginRequest.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken));
        var beginResponse = await Factory.CreateClient().SendAsync(beginRequest, ct);
        var beginBody = await beginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(beginResponse.IsSuccessStatusCode, beginBody);
        Assert.DoesNotContain(new ShortGuid(setup.PositionId).ToString(), beginBody, StringComparison.Ordinal);
        Assert.DoesNotContain(new ShortGuid(second).ToString(), beginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Proof-only position B", beginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("selectionRequired", beginBody, StringComparison.OrdinalIgnoreCase);
        using var begin = JsonDocument.Parse(beginBody);
        var initialCeremony = begin.RootElement.GetProperty("ceremonyId").GetString()!;
        var publicKey = begin.RootElement.GetProperty("publicKey");
        var assertion = setup.Authenticator.CreateAssertionJson(
            publicKey.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}", signCount: 1);

        var proofResponse = await PostTokenAsync(
            StaffingForm(setup.ClientId, initialCeremony, assertion), setup.DeviceKey);
        var proofBody = await proofResponse.Content.ReadAsStringAsync(ct);
        Assert.True(proofResponse.IsSuccessStatusCode, proofBody);
        using var proof = JsonDocument.Parse(proofBody);
        Assert.True(proof.RootElement.TryGetProperty("selectionRequired", out var selectionRequired), proofBody);
        Assert.True(selectionRequired.GetBoolean());
        var continuation = proof.RootElement.GetProperty("ceremonyId").GetString()!;
        var candidates = proof.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Contains(candidates,
            candidate => candidate.GetProperty("id").GetString() == new ShortGuid(setup.PositionId).ToString());
        Assert.Contains(candidates,
            candidate => candidate.GetProperty("id").GetString() == new ShortGuid(second).ToString());

        var selectionForm = new Dictionary<string, string>
        {
            ["grant_type"] = PositionGrantTypes.StaffingSession,
            ["client_id"] = setup.ClientId,
            ["ceremony_id"] = continuation,
            ["position_id"] = new ShortGuid(second).ToString(),
        };
        var selection = await PostTokenAsync(selectionForm, setup.DeviceKey);
        Assert.True(selection.IsSuccessStatusCode, await selection.Content.ReadAsStringAsync(ct));

        using (var scope = Factory.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = Assert.Single(await query.Query<StaffingSession>()
                .Where(item => item.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
            Assert.Equal(second, staffing.PositionPrincipalId);
            Assert.Equal(setup.UserId, staffing.Evidence.UserId);
        }

        var replay = await PostTokenAsync(selectionForm, setup.DeviceKey);
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Password_activation_records_method_evidence_and_refresh_revalidates_its_credential_version()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-password", ct);
        const string password = "PositionPass1234!";
        string accountName;
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(setup.UserId.ToString());
            Assert.NotNull(user);
            Assert.True((await users.AddPasswordAsync(user!, password)).Succeeded);
            accountName = user!.UserName!;
        }

        var policy = await Client.PutAsJsonAsync($"/api/position/{new ShortGuid(setup.PositionId)}", new
        {
            TerminalPolicy = new
            {
                AllowedActivationProofs = new[] { ActivationProofMethodIds.PersonalPassword },
            },
        }, JsonOptions, ct);
        Assert.True(policy.IsSuccessStatusCode, await policy.Content.ReadAsStringAsync(ct));

        var begin = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin")
        {
            Content = JsonContent.Create(new
            {
                MethodId = ActivationProofMethodIds.PersonalPassword,
                AccountName = accountName,
            }),
        };
        begin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        begin.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken));
        var beginResponse = await Factory.CreateClient().SendAsync(begin, ct);
        var beginBody = await beginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(beginResponse.IsSuccessStatusCode, beginBody);
        using var challenge = JsonDocument.Parse(beginBody);
        Assert.Equal(ActivationProofMethodIds.PersonalPassword,
            challenge.RootElement.GetProperty("methodId").GetString());
        var ceremonyId = challenge.RootElement.GetProperty("ceremonyId").GetString()!;

        var redeem = await PostTokenAsync(StaffingForm(
            setup.ClientId, ceremonyId, JsonSerializer.Serialize(new { password })), setup.DeviceKey);
        var redeemBody = await redeem.Content.ReadAsStringAsync(ct);
        Assert.True(redeem.IsSuccessStatusCode, redeemBody);
        using var tokens = JsonDocument.Parse(redeemBody);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = Assert.Single(await session.Query<StaffingSession>()
                .Where(s => s.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
            Assert.Equal(ActivationProofMethodIds.PersonalPassword, staffing.Evidence.MethodId);
            Assert.Equal(setup.UserId, staffing.Evidence.UserId);
            Assert.Equal(new ShortGuid(setup.GrantId).Guid, staffing.Evidence.GrantId);
            Assert.NotNull(staffing.Evidence.CredentialId);
        }

        // A password reset/change rotates the security stamp. Even if an
        // immediate lifecycle hook were missed, refresh must fail closed.
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(setup.UserId.ToString());
            Assert.True((await users.UpdateSecurityStampAsync(user!)).Succeeded);
        }

        var staleRefresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(staleRefresh.IsSuccessStatusCode);
        await AssertSessionEndedAsync(
            setup.TerminalId, StaffingSessionEndReason.ActivationCredentialInvalidated, ct);
    }

    [Fact]
    public async Task Email_otp_activation_opens_a_session_and_refresh_revalidates_the_method()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        const string accountName = "fn-email-otp";
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync(accountName, ct);
        string loginName;
        string email;

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(setup.UserId.ToString());
            Assert.NotNull(user);
            user!.EmailOtpEnabled = true;
            user.EmailConfirmed = true;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            loginName = user.UserName!;
            email = user.Email!;
        }

        var policy = await Client.PutAsJsonAsync($"/api/position/{new ShortGuid(setup.PositionId)}", new
        {
            TerminalPolicy = new
            {
                AllowedActivationProofs = new[] { ActivationProofMethodIds.PersonalEmailOtp },
            },
        }, JsonOptions, ct);
        Assert.True(policy.IsSuccessStatusCode, await policy.Content.ReadAsStringAsync(ct));

        var begin = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin")
        {
            Content = JsonContent.Create(new
            {
                MethodId = ActivationProofMethodIds.PersonalEmailOtp,
                AccountName = loginName,
            }),
        };
        begin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        begin.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken));
        var beginResponse = await Factory.CreateClient().SendAsync(begin, ct);
        var beginBody = await beginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(beginResponse.IsSuccessStatusCode, beginBody);
        using var challenge = JsonDocument.Parse(beginBody);
        Assert.Equal(ActivationProofMethodIds.PersonalEmailOtp,
            challenge.RootElement.GetProperty("methodId").GetString());
        var ceremonyId = challenge.RootElement.GetProperty("ceremonyId").GetString()!;

        var mailbox = Factory.Services.GetRequiredService<InMemoryEmailService>();
        var message = mailbox.GetLastEmailTo(email);
        Assert.NotNull(message);
        var match = System.Text.RegularExpressions.Regex.Match(message!.HtmlBody, @"(\d{6})");
        Assert.True(match.Success, "No six-digit staffing OTP was found in the captured e-mail.");

        var redeem = await PostTokenAsync(StaffingForm(setup.ClientId, ceremonyId,
            JsonSerializer.Serialize(new { code = match.Groups[1].Value })), setup.DeviceKey);
        var redeemBody = await redeem.Content.ReadAsStringAsync(ct);
        Assert.True(redeem.IsSuccessStatusCode, redeemBody);
        using var tokens = JsonDocument.Parse(redeemBody);
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = Assert.Single(await session.Query<StaffingSession>()
                .Where(item => item.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
            Assert.Equal(ActivationProofMethodIds.PersonalEmailOtp, staffing.Evidence.MethodId);
            Assert.Equal(setup.UserId, staffing.Evidence.UserId);
            Assert.Equal(new ShortGuid(setup.GrantId).Guid, staffing.Evidence.GrantId);
        }

        // Even if an immediate invalidation hook were ever missed, refresh
        // must fail closed after the user disables this activation method.
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(setup.UserId.ToString());
            user!.EmailOtpEnabled = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        var staleRefresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(staleRefresh.IsSuccessStatusCode);
        await AssertSessionEndedAsync(
            setup.TerminalId, StaffingSessionEndReason.ActivationCredentialInvalidated, ct);
    }

    [Fact]
    public async Task Position_token_registers_staffs_refreshes_and_revocation_cuts_the_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var setup = await SetUpEnrolledTerminalWithGrantedUserAsync("fn-position-token", ct);

        var policy = await Client.PutAsJsonAsync($"/api/position/{new ShortGuid(setup.PositionId)}", new
        {
            TerminalPolicy = new
            {
                AllowedActivationProofs = new[] { ActivationProofMethodIds.PositionToken },
            },
        }, JsonOptions, ct);
        Assert.True(policy.IsSuccessStatusCode, await policy.Content.ReadAsStringAsync(ct));

        var create = await Client.PostAsJsonAsync(
            $"/api/position/{new ShortGuid(setup.PositionId)}/activation-tokens",
            new { Label = "Staffing position key" }, JsonOptions, ct);
        var createBody = await create.Content.ReadAsStringAsync(ct);
        Assert.True(create.IsSuccessStatusCode, createBody);
        var token = JsonSerializer.Deserialize<ActivationTokenDto>(createBody, JsonOptions)!;
        var tokenGuid = new ShortGuid(token.Id).Guid;

        using var authenticator = new SoftwareWebAuthnAuthenticator(
            Encoding.UTF8.GetBytes(tokenGuid.ToString()));
        var registrationBeginUrl = $"/connect/activation-token/{token.Id}/register/begin";
        var registrationBegin = new HttpRequestMessage(HttpMethod.Post, registrationBeginUrl);
        registrationBegin.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        registrationBegin.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", $"http://localhost{registrationBeginUrl}", DateTimeOffset.UtcNow,
            setup.EnrollmentAccessToken));
        var registrationBeginResponse = await Factory.CreateClient().SendAsync(registrationBegin, ct);
        var registrationBeginBody = await registrationBeginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(registrationBeginResponse.IsSuccessStatusCode, registrationBeginBody);
        using var registration = JsonDocument.Parse(registrationBeginBody);
        var registrationCeremonyId = registration.RootElement.GetProperty("ceremonyId").GetString()!;
        var options = registration.RootElement.GetProperty("options");
        var attestation = authenticator.CreateAttestationJson(
            options.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");
        using var attestationDocument = JsonDocument.Parse(attestation);

        var registrationCompleteUrl = $"/connect/activation-token/{token.Id}/register";
        var registrationComplete = new HttpRequestMessage(HttpMethod.Post, registrationCompleteUrl)
        {
            Content = JsonContent.Create(new
            {
                ceremonyId = registrationCeremonyId,
                attestation = attestationDocument.RootElement.Clone(),
            }),
        };
        registrationComplete.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        registrationComplete.Headers.Add(DpopConstants.HeaderName, setup.DeviceKey.CreateProof(
            "POST", $"http://localhost{registrationCompleteUrl}", DateTimeOffset.UtcNow,
            setup.EnrollmentAccessToken));
        var registrationCompleteResponse = await Factory.CreateClient().SendAsync(registrationComplete, ct);
        Assert.True(registrationCompleteResponse.IsSuccessStatusCode,
            await registrationCompleteResponse.Content.ReadAsStringAsync(ct));

        var begin = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin")
        {
            Content = JsonContent.Create(new { MethodId = ActivationProofMethodIds.PositionToken }),
        };
        begin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        begin.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken));
        var beginResponse = await Factory.CreateClient().SendAsync(begin, ct);
        var beginBody = await beginResponse.Content.ReadAsStringAsync(ct);
        Assert.True(beginResponse.IsSuccessStatusCode, beginBody);
        using var challenge = JsonDocument.Parse(beginBody);
        var ceremonyId = challenge.RootElement.GetProperty("ceremonyId").GetString()!;
        var publicKey = challenge.RootElement.GetProperty("publicKey");
        var assertion = authenticator.CreateAssertionJson(
            publicKey.GetProperty("challenge").GetString()!, RpId, $"https://{RpId}");

        var redeem = await PostTokenAsync(
            StaffingForm(setup.ClientId, ceremonyId, assertion), setup.DeviceKey);
        var redeemBody = await redeem.Content.ReadAsStringAsync(ct);
        Assert.True(redeem.IsSuccessStatusCode, redeemBody);
        using var staffingTokens = JsonDocument.Parse(redeemBody);
        var refreshToken = staffingTokens.RootElement.GetProperty("refresh_token").GetString()!;

        using (var scope = Factory.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var staffing = Assert.Single(await query.Query<StaffingSession>()
                .Where(item => item.TerminalEnrollmentId == setup.TerminalId).ToListAsync(ct));
            Assert.Equal(ActivationProofMethodIds.PositionToken, staffing.Evidence.MethodId);
            Assert.Equal(tokenGuid, staffing.Evidence.ActivationTokenId);
            Assert.NotNull(staffing.Evidence.CredentialId);
            Assert.Null(staffing.Evidence.UserId);
            Assert.Null(staffing.Evidence.GrantId);
        }

        var validRefresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.True(validRefresh.IsSuccessStatusCode, await validRefresh.Content.ReadAsStringAsync(ct));

        var revoke = await Client.PostAsync($"/api/activation-token/{token.Id}/revoke", null, ct);
        Assert.True(revoke.IsSuccessStatusCode, await revoke.Content.ReadAsStringAsync(ct));
        await AssertSessionEndedAsync(
            setup.TerminalId, StaffingSessionEndReason.ActivationTokenRevoked, ct);

        var staleRefresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = setup.ClientId,
        }, setup.DeviceKey);
        Assert.False(staleRefresh.IsSuccessStatusCode);
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

        // A structurally valid resource proof without ath is still invalid:
        // the proof has to be bound to this exact reference access token.
        var noAth = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        noAth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        noAth.Headers.Add(DpopConstants.HeaderName,
            setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow));
        var noAthResponse = await Factory.CreateClient().SendAsync(noAth, ct);
        Assert.Equal(HttpStatusCode.Forbidden, noAthResponse.StatusCode);

        // A proof jti is one-shot across the realm, including resource
        // endpoints (the token endpoint already enforces the same store).
        var replayProof = setup.DeviceKey.CreateProof(
            "POST", BeginEndpoint, DateTimeOffset.UtcNow, setup.EnrollmentAccessToken);
        var firstUse = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        firstUse.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        firstUse.Headers.Add(DpopConstants.HeaderName, replayProof);
        var firstUseResponse = await Factory.CreateClient().SendAsync(firstUse, ct);
        Assert.True(firstUseResponse.IsSuccessStatusCode,
            await firstUseResponse.Content.ReadAsStringAsync(ct));

        var replay = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        replay.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        replay.Headers.Add(DpopConstants.HeaderName, replayProof);
        var replayResponse = await Factory.CreateClient().SendAsync(replay, ct);
        Assert.Equal(HttpStatusCode.Forbidden, replayResponse.StatusCode);
        Assert.Contains("Staffing.DpopReplay", await replayResponse.Content.ReadAsStringAsync(ct));

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
            key.CreateProof("POST", $"http://localhost{url}", DateTimeOffset.UtcNow, accessToken));
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
        string EnrollmentRefreshToken,
        DpopProofBuilder DeviceKey,
        SoftwareWebAuthnAuthenticator Authenticator,
        string Binding,
        string? ClientSecret);

    /// <summary>Position (policy on) + granted user with a seeded RP-ID
    /// passkey + terminal slot enrolled via the full MG-FT-04 device flow.</summary>
    private async Task<StaffingSetup> SetUpEnrolledTerminalWithGrantedUserAsync(
        string accountName,
        CancellationToken ct,
        string binding = DeviceBindingIds.Dpop,
        IReadOnlyList<string>? businessScopes = null,
        IReadOnlyList<string>? appIds = null)
    {
        // Position + terminal slot via the admin API.
        var fnResp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = new
            {
                Enabled = true,
                AllowedDeviceBindings = new[] { binding },
            },
        }, JsonOptions, ct);
        Assert.True(fnResp.IsSuccessStatusCode, await fnResp.Content.ReadAsStringAsync(ct));
        var fnId = new ShortGuid((await fnResp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id).Guid;

        var termResp = await Client.PostAsJsonAsync($"/api/position/{new ShortGuid(fnId)}/terminals",
            new
            {
                DisplayName = "Staff-Terminal",
                Location = "Tor 1",
                WebAuthnRpId = RpId,
                Binding = binding,
                Scopes = businessScopes ?? [],
                AppIds = appIds ?? [],
            }, JsonOptions, ct);
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
            terminal.ClientId,
            binding == DeviceBindingIds.Dpop
                ? deviceKey.CreateProof("POST", DeviceEndpoint, DateTimeOffset.UtcNow)
                : null,
            terminal.ClientSecret);
        var admin = await CreateAuthenticatedClientAsync("tu", "TestPass1234");
        await OpenVerificationAsync(admin, userCode);
        var approve = await SubmitDecisionAsync(admin, userCode);
        Assert.True((int)approve.StatusCode < 400,
            $"approve failed ({(int)approve.StatusCode}): {await approve.Content.ReadAsStringAsync(ct)}");
        var poll = await PostTokenForBindingAsync(new Dictionary<string, string>
        {
            ["grant_type"] = DeviceCodeGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = terminal.ClientId,
        }, binding, deviceKey, terminal.ClientSecret);
        var pollBody = await poll.Content.ReadAsStringAsync(ct);
        Assert.True(poll.IsSuccessStatusCode, $"enrollment poll failed ({(int)poll.StatusCode}): {pollBody}");
        using var tokens = JsonDocument.Parse(pollBody);
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.RootElement.GetProperty("refresh_token").GetString()!;

        return new StaffingSetup(fnId, terminalId, terminal.ClientId, userId, grantId,
            accessToken, refreshToken, deviceKey, authenticator, binding, terminal.ClientSecret);
    }

    private async Task<App> CreateStaffingBusinessResourceAsync(
        string appSlug, string audience, string scopeName, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var appId = Guid.NewGuid();
        var permission = new AppPermission(Guid.NewGuid(), "alarm", "read", null);
        session.Events.StartStream<App>(appId, new AppCreatedEvent(
            appId, appSlug, appSlug, null, [permission], IsSystem: false));
        await session.SaveChangesAsync(ct);

        var apiId = Guid.NewGuid();
        var (api, created) = OAuthApiAggregate.Create(
            apiId, audience, audience, description: null, enabled: true, scopes: []);
        session.Events.StartStream<OAuthApiAggregate>(apiId, created);
        session.Events.Append(apiId, api.SetAppId(appId));
        session.Events.Append(apiId, api.SetPermissionIds([permission.Id]));
        await session.SaveChangesAsync(ct);

        var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
        var scopeResult = await oauth.CreateScopeAsync(new CreateOAuthScopeDto
        {
            Name = scopeName,
            DisplayName = scopeName,
            Resources = [audience],
            AppId = new ShortGuid(appId).ToString(),
        }, ct);
        Assert.False(scopeResult.IsError,
            string.Join(", ", scopeResult.Errors.Select(error => error.Description)));
        return (await session.LoadAsync<App>(appId, ct))!;
    }

    // ─── flow helpers ─────────────────────────────────────────────────────

    private sealed record BeginResult(string CeremonyId, JsonElement Options);

    private async Task<BeginResult> BeginStaffingAsync(StaffingSetup setup, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/staffing/begin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.EnrollmentAccessToken);
        if (setup.Binding == DeviceBindingIds.Dpop)
            request.Headers.Add(DpopConstants.HeaderName,
                setup.DeviceKey.CreateProof("POST", BeginEndpoint, DateTimeOffset.UtcNow,
                    setup.EnrollmentAccessToken));
        var resp = await Factory.CreateClient().SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"staffing begin failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return new BeginResult(
            doc.RootElement.GetProperty("ceremonyId").GetString()!,
            doc.RootElement.GetProperty("publicKey").Clone());
    }

    private Task<HttpResponseMessage> RedeemStaffingAsync(StaffingSetup setup, string ceremonyId, string assertion) =>
        PostTokenForBindingAsync(StaffingForm(setup.ClientId, ceremonyId, assertion), setup);

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

    private Task<HttpResponseMessage> PostTokenAsync(
        Dictionary<string, string> form,
        DpopProofBuilder key) =>
        PostTokenForBindingAsync(form, DeviceBindingIds.Dpop, key, clientSecret: null);

    private Task<HttpResponseMessage> PostTokenForBindingAsync(
        Dictionary<string, string> form,
        StaffingSetup setup) =>
        PostTokenForBindingAsync(form, setup.Binding, setup.DeviceKey, setup.ClientSecret);

    private async Task<HttpResponseMessage> PostTokenForBindingAsync(
        Dictionary<string, string> form,
        string binding,
        DpopProofBuilder key,
        string? clientSecret)
    {
        var values = form.ToList();
        if (binding == DeviceBindingIds.ClientSecret)
        {
            Assert.False(string.IsNullOrWhiteSpace(clientSecret));
            values.Add(new KeyValuePair<string, string>("client_secret", clientSecret!));
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(values),
        };
        if (binding == DeviceBindingIds.Dpop)
            request.Headers.Add(DpopConstants.HeaderName,
                key.CreateProof("POST", TokenEndpoint, DateTimeOffset.UtcNow));
        return await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<(string DeviceCode, string UserCode)> RequestDeviceCodeAsync(
        string clientId,
        string? dpopProof,
        string? clientSecret = null)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
        };
        if (clientSecret is not null)
            values.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = new FormUrlEncodedContent(values),
        };
        if (dpopProof is not null)
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

    private async Task RewriteAsLegacyControlTokenAsync(
        string referenceToken,
        Guid positionId,
        CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await manager.FindByReferenceIdAsync(referenceToken, ct);
        Assert.NotNull(token);
        var descriptor = new OpenIddictTokenDescriptor();
        await manager.PopulateAsync(descriptor, token!, ct);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Payload));

        var current = new JsonWebToken(descriptor.Payload);
        var keyStore = scope.ServiceProvider.GetRequiredService<IRealmKeyStore>();
        var serverOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        var verificationKeys = (await keyStore.GetVerificationKeysAsync(
                TenantConstants.SystemTenantId, ct))
            .Concat(serverOptions.SigningCredentials.Select(item => item.Key))
            .ToArray();
        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(descriptor.Payload, new TokenValidationParameters
        {
            IssuerSigningKeys = verificationKeys,
            TokenDecryptionKeys = serverOptions.EncryptionCredentials.Select(item => item.Key),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
        });
        Assert.True(validation.IsValid, validation.Exception?.ToString());
        var validated = Assert.IsType<JsonWebToken>(validation.SecurityToken);
        var inner = validated.InnerToken ?? validated;
        var payloadJson = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(inner.EncodedPayload));
        var payload = JsonNode.Parse(payloadJson)!.AsObject();
        payload[Claims.Subject] = positionId.ToString();
        payload[PositionTokenClaimTypes.PrincipalType] = PositionPrincipalTypes.Position;

        var signingCredentials = serverOptions.SigningCredentials.FirstOrDefault(
            item => string.Equals(item.Key.KeyId, inner.Kid, StringComparison.Ordinal))
            ?? await keyStore.GetActiveSigningCredentialsAsync(TenantConstants.SystemTenantId, ct);
        var headers = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(current.Typ))
            headers["typ"] = current.Typ;
        var innerHeaders = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(inner.Typ))
            innerHeaders["typ"] = inner.Typ;
        descriptor.Payload = current.IsEncrypted
            ? handler.CreateToken(payload.ToJsonString(), signingCredentials,
                serverOptions.EncryptionCredentials[0], CompressionAlgorithms.Deflate,
                headers, innerHeaders)
            : handler.CreateToken(payload.ToJsonString(), signingCredentials, innerHeaders);
        descriptor.Subject = positionId.ToString();
        await manager.UpdateAsync(token!, descriptor, ct);
    }

    private async Task AssertLegacyControlTokenAsync(
        string referenceToken,
        Guid positionId,
        Guid terminalId,
        CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await manager.FindByReferenceIdAsync(referenceToken, ct);
        Assert.NotNull(token);
        var payload = await manager.GetPayloadAsync(token!, ct);
        Assert.False(string.IsNullOrWhiteSpace(payload));
        var jwt = new JsonWebToken(payload);
        Assert.Equal(positionId.ToString(), jwt.GetClaim(Claims.Subject).Value);
        Assert.Equal(PositionPrincipalTypes.Position,
            jwt.GetClaim(PositionTokenClaimTypes.PrincipalType).Value);
        Assert.Equal(terminalId.ToString(),
            jwt.GetClaim(PositionTokenClaimTypes.TerminalId).Value);
    }

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

        public string CreateProof(string htm, string htu, DateTimeOffset iat, string? accessToken = null)
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
            if (accessToken is not null)
                payload["ath"] = Base64Url.EncodeToString(
                    SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
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
