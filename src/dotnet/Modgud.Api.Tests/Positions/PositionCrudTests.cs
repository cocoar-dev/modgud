using BuildingBlocks.Helper;
using Marten;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Applications;
using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// MG-FT-01 — admin CRUD for <c>PositionPrincipal</c>s: normalization, the
/// shared account-name namespace (Person + ServiceAccount + Position, checked
/// in BOTH directions), the terminal-policy invariants, soft-delete semantics,
/// and the permission gate. The person-side reverse check (a user must not take
/// a position's name) lives in CreateUserCommand/SelfRegistrationService and is
/// shape-identical to the SA-side reverse pinned here; it is not e2e-tested
/// because the effective username depends on the realm registration policy.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PositionCrudTests : IntegrationTestBase
{
    public PositionCrudTests(SharedPostgresFixture fixture) : base(fixture) { }

    /// <summary>Every test pins its own flag state explicitly (the AppSettings
    /// singleton survives the per-test Marten reset) — mirrors the
    /// PageBuilderFeatureFlagTests discipline.</summary>
    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    [Fact]
    public async Task Endpoints_return_404_while_the_feature_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(false); // the shipping default — the feature is dark

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/position", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PostAsJsonAsync("/api/position", new { AccountName = "fn-dark" }, JsonOptions, ct)).StatusCode);
    }

    [Fact]
    public async Task Create_normalises_the_name_and_defaults_to_disabled_terminal_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await Client.PostAsJsonAsync("/api/position",
            new { AccountName = "  Portier.Kunde-XY  ", Purpose = "  Tor 3  " }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({(int)resp.StatusCode}): {body}");

        var created = await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct);
        Assert.NotNull(created);
        Assert.Equal("portier.kunde-xy", created!.AccountName);
        Assert.Equal("Tor 3", created.Purpose);
        Assert.True(created.IsActive);
        // Never terminal-enabled by accident; plan defaults 16 h / 24 h.
        Assert.False(created.TerminalPolicy.Enabled);
        Assert.Equal(16 * 60, created.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(24 * 60, created.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        var get = await Client.GetAsync($"/api/position/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_names_taken_by_person_service_account_or_position()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        // The default admin's Person carries account name "tu".
        var vsPerson = await Client.PostAsJsonAsync("/api/position", new { AccountName = "tu" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsPerson.StatusCode);

        var sa = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = "fn-taken-by-sa" }, JsonOptions, ct);
        Assert.True(sa.IsSuccessStatusCode, $"SA seed failed: {await sa.Content.ReadAsStringAsync(ct)}");
        var vsSa = await Client.PostAsJsonAsync("/api/position", new { AccountName = "FN-Taken-By-SA" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsSa.StatusCode);

        var first = await Client.PostAsJsonAsync("/api/position", new { AccountName = "fn-dup" }, JsonOptions, ct);
        Assert.True(first.IsSuccessStatusCode);
        var vsPosition = await Client.PostAsJsonAsync("/api/position", new { AccountName = "fn-dup" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsPosition.StatusCode);
    }

    [Fact]
    public async Task ServiceAccount_create_rejects_a_name_owned_by_a_position()
    {
        // The REVERSE direction — without it the namespace rule fails open:
        // the position side alone cannot stop an SA from taking its name.
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var fn = await Client.PostAsJsonAsync("/api/position", new { AccountName = "fn-owns-name" }, JsonOptions, ct);
        Assert.True(fn.IsSuccessStatusCode);

        var sa = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = "fn-owns-name" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, sa.StatusCode);
        Assert.Contains("position", await sa.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Update_merges_the_terminal_policy_and_enforces_the_lifetime_ceiling()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var created = await CreatePositionAsync("fn-policy", ct);

        // Enable with custom lifetimes (8 h shift, 10 h ceiling).
        var enable = await Client.PutAsJsonAsync($"/api/position/{created.Id}", new
        {
            TerminalPolicy = new { Enabled = true, StaffingSessionLifetimeMinutes = 480, MaximumStaffingSessionLifetimeMinutes = 600 },
        }, JsonOptions, ct);
        var enabled = await enable.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct);
        Assert.True(enable.IsSuccessStatusCode);
        Assert.True(enabled!.TerminalPolicy.Enabled);
        Assert.Equal(480, enabled.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(600, enabled.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        // Partial update: only the session lifetime — the rest must survive.
        var partial = await Client.PutAsJsonAsync($"/api/position/{created.Id}", new
        {
            TerminalPolicy = new { StaffingSessionLifetimeMinutes = 300 },
        }, JsonOptions, ct);
        var merged = await partial.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct);
        Assert.True(partial.IsSuccessStatusCode);
        Assert.True(merged!.TerminalPolicy.Enabled);
        Assert.Equal(300, merged.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(600, merged.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        // A session lifetime past the absolute ceiling is the invariant the
        // token model leans on (a refresh must never push past the maximum).
        var overCeiling = await Client.PutAsJsonAsync($"/api/position/{created.Id}", new
        {
            TerminalPolicy = new { StaffingSessionLifetimeMinutes = 700 },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, overCeiling.StatusCode);

        var nonPositive = await Client.PutAsJsonAsync($"/api/position/{created.Id}", new
        {
            TerminalPolicy = new { MaximumStaffingSessionLifetimeMinutes = 0 },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, nonPositive.StatusCode);
    }

    [Fact]
    public async Task Update_renames_with_conflict_detection()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var a = await CreatePositionAsync("fn-rename-a", ct);
        await CreatePositionAsync("fn-rename-b", ct);

        var conflict = await Client.PutAsJsonAsync($"/api/position/{a.Id}",
            new { AccountName = "fn-rename-b" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var ok = await Client.PutAsJsonAsync($"/api/position/{a.Id}",
            new { AccountName = "FN-Rename-A2" }, JsonOptions, ct);
        var renamed = await ok.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct);
        Assert.True(ok.IsSuccessStatusCode);
        Assert.Equal("fn-rename-a2", renamed!.AccountName);
    }

    [Fact]
    public async Task Delete_soft_deletes_and_frees_the_name()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var created = await CreatePositionAsync("fn-del", ct);

        var del = await Client.DeleteAsync($"/api/position/{created.Id}", ct);
        Assert.True(del.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/position/{created.Id}", ct)).StatusCode);

        // Soft-deleted rows leave the namespace — the name is reusable.
        var recreate = await Client.PostAsJsonAsync("/api/position", new { AccountName = "fn-del" }, JsonOptions, ct);
        Assert.True(recreate.IsSuccessStatusCode,
            $"recreate after delete failed: {await recreate.Content.ReadAsStringAsync(ct)}");
    }

    [Fact]
    public async Task A_zero_role_user_gets_403_on_read_and_write()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Fn", acronym: "ZF", email: "zerofn@test.com", password: "TestPass1234");
        var zeroClient = await CreateAuthenticatedClientAsync("zf", "TestPass1234");

        Assert.Equal(HttpStatusCode.Forbidden, (await zeroClient.GetAsync("/api/position", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await zeroClient.PostAsJsonAsync("/api/position", new { AccountName = "fn-nope" }, JsonOptions, ct)).StatusCode);
    }

    [Fact]
    public async Task Positions_are_event_sourced_one_event_per_mutation()
    {
        // Pins the persistence model itself: PositionPrincipal documents are the
        // inline projection of a stream (like Person/Group), never direct writes.
        // If someone reverts to session.Store, the stream stays empty and this fails.
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var created = await CreatePositionAsync("fn-stream", ct);

        await Client.PutAsJsonAsync($"/api/position/{created.Id}", new { Purpose = "p2" }, JsonOptions, ct);
        await Client.DeleteAsync($"/api/position/{created.Id}", ct);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IQuerySession>();
        var id = new BuildingBlocks.Helper.ShortGuid(created.Id).Guid;
        var stream = await session.Events.FetchStreamAsync(id, token: ct);
        Assert.Equal(3, stream.Count); // created + updated + deleted
        Assert.Contains(stream, e => e.Data is Modgud.Authorization.Events.PositionPrincipalCreatedEvent);
        Assert.Contains(stream, e => e.Data is Modgud.Authorization.Events.PositionPrincipalUpdatedEvent);
        Assert.Contains(stream, e => e.Data is Modgud.Authorization.Events.PositionPrincipalDeletedEvent);
    }

    /// <summary>
    /// Modal contract rule 5 — a position is creatable as a whole: staged
    /// terminal slots travel in the create body and commit with the position,
    /// exactly like the service account's initial credential. Each slot brings
    /// its managed OAuth client along in that same unit of work.
    /// </summary>
    [Fact]
    public async Task Create_sets_up_staged_terminal_slots_in_the_same_save()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = "portier.staged",
            TerminalPolicy = new { Enabled = true },
            Terminals = new[]
            {
                new { DisplayName = "Terminal links", Location = "Tor 3", WebAuthnRpId = "alerthub.example.com" },
                new { DisplayName = "Terminal rechts", Location = (string?)null, WebAuthnRpId = "alerthub.example.com" },
            },
        }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({(int)resp.StatusCode}): {body}");

        var created = (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!;
        Assert.True(created.TerminalPolicy.Enabled);

        var slots = await Client.GetFromJsonAsync<List<TerminalDto>>(
            $"/api/position/{created.Id}/terminals", JsonOptions, ct);
        Assert.NotNull(slots);
        Assert.Equal(2, slots!.Count);
        Assert.All(slots, s => Assert.Equal(TerminalEnrollmentStatus.Pending, s.Status));
        Assert.All(slots, s => Assert.StartsWith("portier.staged.terminal.", s.ClientId));
        Assert.Equal("Tor 3", slots.Single(s => s.DisplayName == "Terminal links").Location);

        // Every slot's managed client committed with it — no half-created pair.
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        foreach (var slot in slots)
        {
            var client = (await session.Query<OAuthApplicationState>()
                .Where(c => c.ClientId == slot.ClientId).ToListAsync(ct)).Single();
            Assert.Equal(new ShortGuid(created.Id).Guid, client.LinkedPositionPrincipalId);
            Assert.Equal(new ShortGuid(slot.Id).Guid, client.ManagedTerminalEnrollmentId);
        }
    }

    /// <summary>
    /// §15.4 — deleting a position takes its terminal slots with it. Without the
    /// cascade a soft-deleted position left its slots Pending/Active and their
    /// managed OAuth clients registered, pointing at a principal that no longer
    /// exists (found by clicking through the running container).
    /// </summary>
    [Fact]
    public async Task Deleting_a_position_revokes_its_slots_and_deletes_their_clients()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = "portier.cascade",
            TerminalPolicy = new { Enabled = true },
            Terminals = new[] { new { DisplayName = "Terminal links", WebAuthnRpId = "alerthub.example.com" } },
        }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed: {await resp.Content.ReadAsStringAsync(ct)}");
        var created = (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!;

        var slot = (await Client.GetFromJsonAsync<List<TerminalDto>>(
            $"/api/position/{created.Id}/terminals", JsonOptions, ct))!.Single();

        var delete = await Client.DeleteAsync($"/api/position/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var enrollment = await session.LoadAsync<TerminalEnrollment>(new ShortGuid(slot.Id).Guid, ct);
        Assert.NotNull(enrollment);
        Assert.Equal(TerminalEnrollmentStatus.Revoked, enrollment!.Status);

        // Same shape as the per-slot revoke: the client document stays for audit
        // but is soft-deleted, so nothing can authenticate with it any more.
        var client = (await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId == slot.ClientId).ToListAsync(ct)).Single();
        Assert.True(client.IsDeleted);
    }

    /// <summary>Plan §4.1 holds at create time too: slots need terminal use.
    /// The rejection is all-or-nothing — no position, no orphaned client.</summary>
    [Fact]
    public async Task Create_rejects_staged_slots_while_terminal_use_stays_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = "portier.noterminals",
            Terminals = new[] { new { DisplayName = "Terminal links", WebAuthnRpId = "alerthub.example.com" } },
        }, JsonOptions, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("TerminalPolicyDisabled", await resp.Content.ReadAsStringAsync(ct));

        var all = await Client.GetFromJsonAsync<List<PositionPrincipalDto>>("/api/position", JsonOptions, ct);
        Assert.DoesNotContain(all!, p => p.AccountName == "portier.noterminals");

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        Assert.Empty(await session.Query<OAuthApplicationState>()
            .Where(c => c.ClientId.StartsWith("portier.noterminals.")).ToListAsync(ct));
    }

    private async Task<PositionPrincipalDto> CreatePositionAsync(string accountName, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/position", new { AccountName = accountName }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create '{accountName}' failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!;
    }
}
