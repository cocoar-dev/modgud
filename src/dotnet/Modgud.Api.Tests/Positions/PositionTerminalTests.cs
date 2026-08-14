using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// MG-FT-03 — terminal slots: a slot create commits enrollment + its
/// terminal-managed public client atomically with the fixed profile (public,
/// secretless, DPoP, reference tokens, RP-ID, exactly the three terminal
/// grants); the generic OAuth admin surface is read-only for that client; and
/// the Pending/Disabled/Revoked lifecycle is idempotent with revoked terminal.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PositionTerminalTests : IntegrationTestBase
{
    public PositionTerminalTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task Terminals_are_dark_while_the_feature_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(false);
        var anyId = new ShortGuid(Guid.NewGuid()).ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/position/{anyId}/terminals", ct)).StatusCode);
    }

    [Fact]
    public async Task Slot_create_requires_the_terminal_policy_to_be_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-policy-off", terminalEnabled: false, ct);

        var resp = await Client.PostAsJsonAsync($"/api/position/{fn}/terminals",
            new { DisplayName = "Links", WebAuthnRpId = RpId }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Terminal.TerminalPolicyDisabled", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_slot_creates_the_managed_public_client_atomically_with_the_fixed_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-slot", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Terminal links", ct);

        Assert.Equal(TerminalEnrollmentStatus.Pending, terminal.Status);
        Assert.False(terminal.Enrolled);
        Assert.StartsWith("fn-slot.terminal.", terminal.ClientId);
        Assert.Equal(RpId, terminal.WebAuthnRpId);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var client = (await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == terminal.ClientId)
            .ToListAsync(ct)).Single();

        // The fixed terminal profile, field by field.
        Assert.Equal("public", client.ClientType);
        Assert.Equal(new ShortGuid(terminal.PositionId).Guid, client.LinkedPositionPrincipalId);
        Assert.Equal(new ShortGuid(terminal.Id).Guid, client.ManagedTerminalEnrollmentId);
        Assert.Null(client.LinkedServiceAccountId);
        Assert.Equal(AccessTokenType.Reference.ToString(), client.Settings[OAuthApplicationSettingKeys.AccessTokenType]);
        Assert.Equal(RpId, client.Settings[OAuthApplicationSettingKeys.WebAuthnRpId]);
        Assert.True(ReadBoolProp(client.Properties[OAuthApplicationPropertyKeys.RequireDpop]));
        Assert.False(ReadBoolProp(client.Properties[OAuthApplicationPropertyKeys.RequireClientSecret]));

        // Exactly the three terminal grants — nothing else.
        var grantPermissions = client.Permissions.Where(p => p.StartsWith("gt:")).OrderBy(p => p).ToList();
        Assert.Equal(
        [
            "gt:refresh_token",
            "gt:" + PositionGrantTypes.StaffingSession,
            "gt:urn:ietf:params:oauth:grant-type:device_code",
        ], grantPermissions.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task The_generic_oauth_admin_surface_is_read_only_for_terminal_clients()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-locked", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Locked", ct);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var appId = (await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == terminal.ClientId).ToListAsync(ct)).Single().Id;
        var adminId = appId.ToString(); // the generic admin surface takes raw Guids

        var put = await Client.PutAsJsonAsync($"/api/admin/oauth/clients/{adminId}",
            new { DisplayName = "hijack" }, JsonOptions, ct);
        Assert.False(put.IsSuccessStatusCode);
        Assert.Contains("owned by a position terminal", await put.Content.ReadAsStringAsync(ct));

        var delete = await Client.DeleteAsync($"/api/admin/oauth/clients/{adminId}", ct);
        Assert.False(delete.IsSuccessStatusCode);
        Assert.Contains("owned by a position terminal", await delete.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task The_lifecycle_is_idempotent_and_revoked_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-lifecycle-t", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Zyklus", ct);

        var disabled = await TransitionAsync(fn, terminal.Id, "disable", ct);
        Assert.Equal(TerminalEnrollmentStatus.Disabled, disabled.Status);
        Assert.False(await IsClientEnabledAsync(terminal.ClientId, ct));

        // Idempotent no-op.
        Assert.Equal(TerminalEnrollmentStatus.Disabled, (await TransitionAsync(fn, terminal.Id, "disable", ct)).Status);

        // Reactivate goes back to Pending — no key was ever enrolled.
        var reactivated = await TransitionAsync(fn, terminal.Id, "reactivate", ct);
        Assert.Equal(TerminalEnrollmentStatus.Pending, reactivated.Status);
        Assert.True(await IsClientEnabledAsync(terminal.ClientId, ct));

        var revoked = await TransitionAsync(fn, terminal.Id, "revoke", ct);
        Assert.Equal(TerminalEnrollmentStatus.Revoked, revoked.Status);
        Assert.NotNull(revoked.RevokedAt);

        // The client died with its slot.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var client = (await session.Query<OAuthApplicationState>()
                .Where(c => c.ClientId == terminal.ClientId).ToListAsync(ct)).Single();
            Assert.True(client.IsDeleted);
        }

        // Revoke is idempotent; everything else on a revoked slot is a 409.
        Assert.Equal(TerminalEnrollmentStatus.Revoked, (await TransitionAsync(fn, terminal.Id, "revoke", ct)).Status);
        var reject = await Client.PostAsync($"/api/position/{fn}/terminals/{terminal.Id}/disable", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
    }

    [Fact]
    public async Task Two_slots_get_two_distinct_clients()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-two-slots", terminalEnabled: true, ct);
        var left = await CreateTerminalAsync(fn, "Links", ct);
        var right = await CreateTerminalAsync(fn, "Rechts", ct);

        Assert.NotEqual(left.ClientId, right.ClientId);
        var list = await Client.GetFromJsonAsync<List<TerminalDto>>($"/api/position/{fn}/terminals", JsonOptions, ct);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async Task Slots_are_event_sourced_one_event_per_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-slot-stream", terminalEnabled: true, ct);
        var terminal = await CreateTerminalAsync(fn, "Stream", ct);
        await TransitionAsync(fn, terminal.Id, "disable", ct);
        await TransitionAsync(fn, terminal.Id, "disable", ct); // no-op must not append
        await TransitionAsync(fn, terminal.Id, "reactivate", ct);
        await TransitionAsync(fn, terminal.Id, "revoke", ct);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(new ShortGuid(terminal.Id).Guid, token: ct);
        Assert.Equal(4, stream.Count); // created + disabled + reactivated + revoked
    }

    [Fact]
    public async Task A_zero_role_user_gets_403()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Terminal", acronym: "ZT", email: "zeroterminal@test.com", password: "TestPass1234");
        var zeroClient = await CreateAuthenticatedClientAsync("zt", "TestPass1234");
        var anyId = new ShortGuid(Guid.NewGuid()).ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await zeroClient.GetAsync($"/api/position/{anyId}/terminals", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeroClient.PostAsJsonAsync($"/api/position/{anyId}/terminals",
            new { DisplayName = "x", WebAuthnRpId = RpId }, JsonOptions, ct)).StatusCode);
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private async Task<string> CreatePositionAsync(string accountName, bool terminalEnabled, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = terminalEnabled ? new { Enabled = true } : null,
        }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id;
    }

    private async Task<TerminalDto> CreateTerminalAsync(string positionId, string displayName, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync($"/api/position/{positionId}/terminals",
            new { DisplayName = displayName, Location = "Tor 3", WebAuthnRpId = RpId }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"terminal create failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
    }

    private async Task<TerminalDto> TransitionAsync(string positionId, string terminalId, string action, CancellationToken ct)
    {
        var resp = await Client.PostAsync($"/api/position/{positionId}/terminals/{terminalId}/{action}", null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"{action} failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<TerminalDto>(JsonOptions, ct))!;
    }

    private async Task<bool> IsClientEnabledAsync(string clientId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var client = (await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == clientId).ToListAsync(ct)).Single();
        return ReadBoolProp(client.Properties[OAuthApplicationPropertyKeys.Enabled]);
    }

    /// <summary>Marten hands persisted boolean properties back as a boxed bool
    /// or a JsonElement depending on the serializer — accept both (mirrors the
    /// production ReadBool in DpopProofValidationHandler).</summary>
    private static bool ReadBoolProp(object? raw) => raw switch
    {
        bool b => b,
        JsonElement e => e.ValueKind is JsonValueKind.True,
        _ => false,
    };
}
