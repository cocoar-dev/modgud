using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Functions;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Functions;

/// <summary>
/// MG-FT-01 — admin CRUD for <c>FunctionPrincipal</c>s: normalization, the
/// shared account-name namespace (Person + ServiceAccount + Function, checked
/// in BOTH directions), the terminal-policy invariants, soft-delete semantics,
/// and the permission gate. The person-side reverse check (a user must not take
/// a function's name) lives in CreateUserCommand/SelfRegistrationService and is
/// shape-identical to the SA-side reverse pinned here; it is not e2e-tested
/// because the effective username depends on the realm registration policy.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FunctionCrudTests : IntegrationTestBase
{
    public FunctionCrudTests(SharedPostgresFixture fixture) : base(fixture) { }

    /// <summary>Every test pins its own flag state explicitly (the AppSettings
    /// singleton survives the per-test Marten reset) — mirrors the
    /// PageBuilderFeatureFlagTests discipline.</summary>
    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.FunctionTerminals = enabled;

    [Fact]
    public async Task Endpoints_return_404_while_the_feature_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(false); // the shipping default — the feature is dark

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/function", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PostAsJsonAsync("/api/function", new { AccountName = "fn-dark" }, JsonOptions, ct)).StatusCode);
    }

    [Fact]
    public async Task Create_normalises_the_name_and_defaults_to_disabled_terminal_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var resp = await Client.PostAsJsonAsync("/api/function",
            new { AccountName = "  Portier.Kunde-XY  ", Purpose = "  Tor 3  " }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create failed ({(int)resp.StatusCode}): {body}");

        var created = await resp.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct);
        Assert.NotNull(created);
        Assert.Equal("portier.kunde-xy", created!.AccountName);
        Assert.Equal("Tor 3", created.Purpose);
        Assert.True(created.IsActive);
        // Never terminal-enabled by accident; plan defaults 16 h / 24 h.
        Assert.False(created.TerminalPolicy.Enabled);
        Assert.Equal(16 * 60, created.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(24 * 60, created.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        var get = await Client.GetAsync($"/api/function/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_names_taken_by_person_service_account_or_function()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        // The default admin's Person carries account name "tu".
        var vsPerson = await Client.PostAsJsonAsync("/api/function", new { AccountName = "tu" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsPerson.StatusCode);

        var sa = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = "fn-taken-by-sa" }, JsonOptions, ct);
        Assert.True(sa.IsSuccessStatusCode, $"SA seed failed: {await sa.Content.ReadAsStringAsync(ct)}");
        var vsSa = await Client.PostAsJsonAsync("/api/function", new { AccountName = "FN-Taken-By-SA" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsSa.StatusCode);

        var first = await Client.PostAsJsonAsync("/api/function", new { AccountName = "fn-dup" }, JsonOptions, ct);
        Assert.True(first.IsSuccessStatusCode);
        var vsFunction = await Client.PostAsJsonAsync("/api/function", new { AccountName = "fn-dup" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, vsFunction.StatusCode);
    }

    [Fact]
    public async Task ServiceAccount_create_rejects_a_name_owned_by_a_function()
    {
        // The REVERSE direction — without it the namespace rule fails open:
        // the function side alone cannot stop an SA from taking its name.
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);

        var fn = await Client.PostAsJsonAsync("/api/function", new { AccountName = "fn-owns-name" }, JsonOptions, ct);
        Assert.True(fn.IsSuccessStatusCode);

        var sa = await Client.PostAsJsonAsync("/api/service-account", new { AccountName = "fn-owns-name" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, sa.StatusCode);
        Assert.Contains("function", await sa.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Update_merges_the_terminal_policy_and_enforces_the_lifetime_ceiling()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var created = await CreateFunctionAsync("fn-policy", ct);

        // Enable with custom lifetimes (8 h shift, 10 h ceiling).
        var enable = await Client.PutAsJsonAsync($"/api/function/{created.Id}", new
        {
            TerminalPolicy = new { Enabled = true, StaffingSessionLifetimeMinutes = 480, MaximumStaffingSessionLifetimeMinutes = 600 },
        }, JsonOptions, ct);
        var enabled = await enable.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct);
        Assert.True(enable.IsSuccessStatusCode);
        Assert.True(enabled!.TerminalPolicy.Enabled);
        Assert.Equal(480, enabled.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(600, enabled.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        // Partial update: only the session lifetime — the rest must survive.
        var partial = await Client.PutAsJsonAsync($"/api/function/{created.Id}", new
        {
            TerminalPolicy = new { StaffingSessionLifetimeMinutes = 300 },
        }, JsonOptions, ct);
        var merged = await partial.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct);
        Assert.True(partial.IsSuccessStatusCode);
        Assert.True(merged!.TerminalPolicy.Enabled);
        Assert.Equal(300, merged.TerminalPolicy.StaffingSessionLifetimeMinutes);
        Assert.Equal(600, merged.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes);

        // A session lifetime past the absolute ceiling is the invariant the
        // token model leans on (a refresh must never push past the maximum).
        var overCeiling = await Client.PutAsJsonAsync($"/api/function/{created.Id}", new
        {
            TerminalPolicy = new { StaffingSessionLifetimeMinutes = 700 },
        }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, overCeiling.StatusCode);

        var nonPositive = await Client.PutAsJsonAsync($"/api/function/{created.Id}", new
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
        var a = await CreateFunctionAsync("fn-rename-a", ct);
        await CreateFunctionAsync("fn-rename-b", ct);

        var conflict = await Client.PutAsJsonAsync($"/api/function/{a.Id}",
            new { AccountName = "fn-rename-b" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var ok = await Client.PutAsJsonAsync($"/api/function/{a.Id}",
            new { AccountName = "FN-Rename-A2" }, JsonOptions, ct);
        var renamed = await ok.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct);
        Assert.True(ok.IsSuccessStatusCode);
        Assert.Equal("fn-rename-a2", renamed!.AccountName);
    }

    [Fact]
    public async Task Delete_soft_deletes_and_frees_the_name()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var created = await CreateFunctionAsync("fn-del", ct);

        var del = await Client.DeleteAsync($"/api/function/{created.Id}", ct);
        Assert.True(del.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/function/{created.Id}", ct)).StatusCode);

        // Soft-deleted rows leave the namespace — the name is reusable.
        var recreate = await Client.PostAsJsonAsync("/api/function", new { AccountName = "fn-del" }, JsonOptions, ct);
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

        Assert.Equal(HttpStatusCode.Forbidden, (await zeroClient.GetAsync("/api/function", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await zeroClient.PostAsJsonAsync("/api/function", new { AccountName = "fn-nope" }, JsonOptions, ct)).StatusCode);
    }

    private async Task<FunctionPrincipalDto> CreateFunctionAsync(string accountName, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/function", new { AccountName = accountName }, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"create '{accountName}' failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct))!;
    }
}
