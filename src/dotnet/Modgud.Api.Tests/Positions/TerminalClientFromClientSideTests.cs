using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.DTOs.User;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// MG-FT — the client-side terminal create ("wie in Service Accounts"): the
/// generic admin client create diverts to the terminal path whenever the
/// staffing grant or a position link appears. The rule mirrors the
/// client_credentials ⇔ ServiceAccount coupling: a staffing client must
/// reference OR inline-create a Position, never both, and the fixed terminal
/// profile plus the slot land in the SAME save.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TerminalClientFromClientSideTests : IntegrationTestBase
{
    public TerminalClientFromClientSideTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string RpId = "alerthub.localhost";
    private const string StaffingGrant = "urn:cocoar:params:oauth:grant-type:staffing";

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task The_client_side_create_is_dark_while_the_feature_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(false);
        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            NewPosition = new { AccountName = "tc-dark", TerminalPolicy = new { Enabled = true } },
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_staffing_client_with_a_linked_position_creates_the_slot_atomically()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-linked", terminalEnabled: true, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "ignored-by-the-terminal-path",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            LinkedPositionPrincipalId = fn,
            TerminalDisplayName = "Terminal links",
            TerminalLocation = "Tor 3",
            WebAuthnRpId = RpId,
        }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({(int)resp.StatusCode}): {body}");
        var created = JsonSerializer.Deserialize<JsonElement>(body);

        // ClientId follows the convention, never the caller's value.
        var clientId = created.GetProperty("Client").GetProperty("ClientId").GetString()!;
        Assert.StartsWith("tc-linked.terminal.", clientId);
        // Nulls may be omitted from the payload entirely — assert "absent or null".
        Assert.False(created.TryGetProperty("ClientSecret", out var secret) && secret.ValueKind is not JsonValueKind.Null);
        Assert.False(created.TryGetProperty("CreatedPosition", out var inlinePosition) && inlinePosition.ValueKind is not JsonValueKind.Null);
        var terminalId = created.GetProperty("CreatedTerminalId").GetString();
        Assert.False(string.IsNullOrEmpty(terminalId));

        // The slot exists on the position, wired to this client.
        var slots = await Client.GetFromJsonAsync<List<TerminalDto>>($"/api/position/{fn}/terminals", JsonOptions, ct);
        var slot = Assert.Single(slots!);
        Assert.Equal(terminalId, slot.Id);
        Assert.Equal(clientId, slot.ClientId);
        Assert.Equal("Terminal links", slot.DisplayName);
        Assert.Equal("Tor 3", slot.Location);
        Assert.Equal(RpId, slot.WebAuthnRpId);
        Assert.Equal(TerminalEnrollmentStatus.Pending, slot.Status);

        // The fixed terminal profile — same shape the slot-side create pins.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var client = (await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == clientId).ToListAsync(ct)).Single();
        Assert.Equal("public", client.ClientType);
        Assert.Equal(new ShortGuid(fn).Guid, client.LinkedPositionPrincipalId);
        Assert.Equal(new ShortGuid(terminalId!).Guid, client.ManagedTerminalEnrollmentId);
        Assert.Null(client.LinkedServiceAccountId);
        Assert.Equal(AccessTokenType.Reference.ToString(), client.Settings[OAuthApplicationSettingKeys.AccessTokenType]);
        Assert.Equal(RpId, client.Settings[OAuthApplicationSettingKeys.WebAuthnRpId]);
        Assert.True(ReadBoolProp(client.Properties[OAuthApplicationPropertyKeys.RequireDpop]));
        Assert.False(ReadBoolProp(client.Properties[OAuthApplicationPropertyKeys.RequireClientSecret]));
        Assert.Equal(
        [
            "gt:refresh_token",
            "gt:" + PositionGrantTypes.StaffingSession,
            "gt:urn:ietf:params:oauth:grant-type:device_code",
        ], client.Permissions.Where(p => p.StartsWith("gt:")).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task A_staffing_client_with_an_inline_position_creates_position_slot_and_client_in_one_save()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant, "refresh_token" },
            NewPosition = new
            {
                AccountName = "tc-inline",
                Purpose = "Pförtner Kunde XY",
                TerminalPolicy = new { Enabled = true, StaffingSessionLifetimeMinutes = 480 },
            },
            TerminalDisplayName = "Empfang",
            WebAuthnRpId = RpId,
        }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({(int)resp.StatusCode}): {body}");
        var created = JsonSerializer.Deserialize<JsonElement>(body);

        var position = created.GetProperty("CreatedPosition");
        Assert.Equal(JsonValueKind.Object, position.ValueKind);
        var positionId = position.GetProperty("Id").GetString()!;
        Assert.Equal("tc-inline", position.GetProperty("AccountName").GetString());

        // The position is real, terminal-enabled, and carries the slot.
        var loaded = await Client.GetFromJsonAsync<PositionPrincipalDto>($"/api/position/{positionId}", JsonOptions, ct);
        Assert.True(loaded!.TerminalPolicy.Enabled);
        Assert.Equal(480, loaded.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal("Pförtner Kunde XY", loaded.Purpose);

        var slots = await Client.GetFromJsonAsync<List<TerminalDto>>($"/api/position/{positionId}/terminals", JsonOptions, ct);
        var slot = Assert.Single(slots!);
        Assert.Equal("Empfang", slot.DisplayName);
        Assert.StartsWith("tc-inline.terminal.", slot.ClientId);
        Assert.Equal(created.GetProperty("Client").GetProperty("ClientId").GetString(), slot.ClientId);
    }

    [Fact]
    public async Task An_inline_position_stages_grant_users_in_the_same_save()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var user = await CreateUserAsync("tcg", ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            NewPosition = new
            {
                AccountName = "tc-grants",
                TerminalPolicy = new { Enabled = true },
                GrantUserIds = new[] { user },
            },
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        var created = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        var positionId = created.GetProperty("CreatedPosition").GetProperty("Id").GetString();

        var grants = await Client.GetFromJsonAsync<List<PositionGrantDto>>($"/api/position/{positionId}/grants", JsonOptions, ct);
        var grant = Assert.Single(grants!);
        Assert.Equal(user, grant.UserId);
        Assert.Equal(PositionGrantStatus.Active, grant.Status);
    }

    [Fact]
    public async Task A_rejected_inline_create_leaves_nothing_behind()
    {
        // All-or-nothing: the slot's RP-ID is missing, so position AND client
        // must not exist afterwards.
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            NewPosition = new { AccountName = "tc-atomic", TerminalPolicy = new { Enabled = true } },
            TerminalDisplayName = "Links",
            WebAuthnRpId = "",
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("WebAuthn RP ID is required", await resp.Content.ReadAsStringAsync(ct));

        // The account name is still free — the position was never committed.
        var retry = await Client.PostAsJsonAsync("/api/position",
            new { AccountName = "tc-atomic" }, JsonOptions, ct);
        Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Link_and_inline_position_together_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-both", terminalEnabled: true, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            LinkedPositionPrincipalId = fn,
            NewPosition = new { AccountName = "tc-both-b", TerminalPolicy = new { Enabled = true } },
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("not both", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task The_staffing_grant_without_a_position_link_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("must reference LinkedPositionPrincipalId", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_position_link_without_the_staffing_grant_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-nogrant", terminalEnabled: true, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { "urn:ietf:params:oauth:grant-type:device_code" },
            LinkedPositionPrincipalId = fn,
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("must carry the terminal grants", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Grants_outside_the_terminal_profile_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-foreign", terminalEnabled: true, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant, "client_credentials" },
            LinkedPositionPrincipalId = fn,
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("allowed grants are exactly", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_position_with_terminal_use_off_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-policy-off", terminalEnabled: false, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            LinkedPositionPrincipalId = fn,
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("terminal use switched off", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task The_terminal_display_name_is_required()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("tc-noname", terminalEnabled: true, ct);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            LinkedPositionPrincipalId = fn,
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("needs TerminalDisplayName", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task An_inline_position_must_not_stage_its_own_terminal_slots()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await PostClientAsync(new
        {
            ClientId = "",
            ClientType = "public",
            AllowedGrantTypes = new[] { StaffingGrant },
            NewPosition = new
            {
                AccountName = "tc-doubleslot",
                TerminalPolicy = new { Enabled = true },
                Terminals = new[] { new { DisplayName = "Extra", WebAuthnRpId = RpId } },
            },
            TerminalDisplayName = "Links",
            WebAuthnRpId = RpId,
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("cannot stage terminal slots", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Adding_the_staffing_grant_via_put_is_rejected()
    {
        // The UpdateDto carries no position link, so a PUT that adds the
        // staffing grant would mint a staffing client with no position — the
        // same guard shape as client_credentials-via-PUT.
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var createResp = await PostClientAsync(new
        {
            ClientId = "tc-put-guard",
            ClientType = "public",
            AllowedGrantTypes = new[] { "authorization_code" },
            RedirectUris = new[] { "https://app.localhost/cb" },
        }, ct);
        Assert.True(createResp.IsSuccessStatusCode, await createResp.Content.ReadAsStringAsync(ct));
        var created = JsonSerializer.Deserialize<JsonElement>(await createResp.Content.ReadAsStringAsync(ct));
        var id = created.GetProperty("Client").GetProperty("Id").GetString();

        var put = await Client.PutAsJsonAsync($"/api/admin/oauth/clients/{id}",
            new { AllowedGrantTypes = new[] { "authorization_code", StaffingGrant } }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("must reference LinkedPositionPrincipalId", await put.Content.ReadAsStringAsync(ct));
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostClientAsync(object dto, CancellationToken ct) =>
        Client.PostAsJsonAsync("/api/admin/oauth/clients", dto, JsonOptions, ct);

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

    private async Task<string> CreateUserAsync(string acronym, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/user", new UserCreateDto
        {
            Firstname = "Terminal",
            Lastname = acronym.ToUpperInvariant(),
            Acronym = acronym.ToUpperInvariant(),
            Email = $"{acronym}@terminal.test",
            IsActive = true,
        }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct))!.Id!;
    }

    /// <summary>Marten hands persisted boolean properties back as a boxed bool
    /// or a JsonElement depending on the serializer — accept both.</summary>
    private static bool ReadBoolProp(object? raw) => raw switch
    {
        bool b => b,
        JsonElement e => e.ValueKind is JsonValueKind.True,
        _ => false,
    };
}
