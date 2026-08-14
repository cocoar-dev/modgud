using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Functions;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Domain.FunctionTerminals;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Functions;

/// <summary>
/// MG-FT-02 — FunctionActivationGrants: issue/list with user display data and
/// the passkey indicator, the (function, user) uniqueness rule across
/// non-revoked grants, idempotent suspend/resume/revoke with revoked as a
/// terminal state, re-grant after revoke, and the event-stream pin.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FunctionGrantTests : IntegrationTestBase
{
    public FunctionGrantTests(SharedPostgresFixture fixture) : base(fixture) { }

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.FunctionTerminals = enabled;

    [Fact]
    public async Task Grants_are_dark_while_the_feature_flag_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(false);
        var anyId = new ShortGuid(Guid.NewGuid()).ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/function/{anyId}/grants", ct)).StatusCode);
    }

    [Fact]
    public async Task Three_users_get_active_grants_and_the_list_carries_their_display_data()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-grants", ct);

        foreach (var name in new[] { "ga", "gb", "gc" })
        {
            var user = await CreateUserAsync(name, ct);
            var resp = await Client.PostAsJsonAsync($"/api/function/{fn}/grants",
                new { UserId = user }, JsonOptions, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.True(resp.IsSuccessStatusCode, $"issue for {name} failed ({(int)resp.StatusCode}): {body}");
        }

        var list = await Client.GetFromJsonAsync<List<FunctionGrantDto>>($"/api/function/{fn}/grants", JsonOptions, ct);
        Assert.Equal(3, list!.Count);
        Assert.All(list, g => Assert.Equal(FunctionActivationGrantStatus.Active, g.Status));
        Assert.All(list, g => Assert.False(string.IsNullOrEmpty(g.UserAccountName)));
    }

    [Fact]
    public async Task A_second_live_grant_for_the_same_pair_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-dup-grant", ct);
        var user = await CreateUserAsync("gd", ct);

        Assert.True((await Client.PostAsJsonAsync($"/api/function/{fn}/grants", new { UserId = user }, JsonOptions, ct)).IsSuccessStatusCode);
        var dup = await Client.PostAsJsonAsync($"/api/function/{fn}/grants", new { UserId = user }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task An_inactive_user_cannot_receive_a_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-inactive-user", ct);
        var user = await CreateUserAsync("gi", ct, isActive: false);

        var resp = await Client.PostAsJsonAsync($"/api/function/{fn}/grants", new { UserId = user }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("inactive", await resp.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suspend_resume_revoke_are_idempotent_and_revoked_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-lifecycle", ct);
        var user = await CreateUserAsync("gl", ct);
        var grant = await IssueAsync(fn, user, ct);

        Assert.Equal(FunctionActivationGrantStatus.Suspended, (await PostTransitionAsync(fn, grant, "suspend", ct)).Status);
        // Idempotent: same request again is a 200 no-op, not an error.
        Assert.Equal(FunctionActivationGrantStatus.Suspended, (await PostTransitionAsync(fn, grant, "suspend", ct)).Status);
        Assert.Equal(FunctionActivationGrantStatus.Active, (await PostTransitionAsync(fn, grant, "resume", ct)).Status);
        var revoked = await PostTransitionAsync(fn, grant, "revoke", ct);
        Assert.Equal(FunctionActivationGrantStatus.Revoked, revoked.Status);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal(FunctionActivationGrantStatus.Revoked, (await PostTransitionAsync(fn, grant, "revoke", ct)).Status);

        // Terminal: a revoked grant cannot be resumed or suspended...
        var resume = await Client.PostAsync($"/api/function/{fn}/grants/{grant}/resume", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);

        // ...but the PAIR is re-grantable with a fresh grant (fresh audit trail).
        var reissue = await Client.PostAsJsonAsync($"/api/function/{fn}/grants", new { UserId = user }, JsonOptions, ct);
        Assert.True(reissue.IsSuccessStatusCode, await reissue.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Grants_are_event_sourced_one_event_per_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-grant-stream", ct);
        var user = await CreateUserAsync("gs", ct);
        var grant = await IssueAsync(fn, user, ct);
        await PostTransitionAsync(fn, grant, "suspend", ct);
        await PostTransitionAsync(fn, grant, "suspend", ct); // no-op must NOT append
        await PostTransitionAsync(fn, grant, "revoke", ct);

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(new ShortGuid(grant).Guid, token: ct);
        Assert.Equal(3, stream.Count); // issued + suspended + revoked — no no-op events
    }

    [Fact]
    public async Task The_passkey_indicator_reflects_credentials_and_narrows_by_rp_id()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreateFunctionAsync("fn-passkey-flag", ct);
        var withKey = await CreateUserAsync("gp", ct);
        var withoutKey = await CreateUserAsync("gq", ct);
        await IssueAsync(fn, withKey, ct);
        await IssueAsync(fn, withoutKey, ct);
        await SeedPasskeyAsync(new ShortGuid(withKey).Guid, rpId: "alerthub.localhost", ct);

        var all = await Client.GetFromJsonAsync<List<FunctionGrantDto>>($"/api/function/{fn}/grants", JsonOptions, ct);
        Assert.True(all!.Single(g => g.UserId == withKey).UserHasPasskey);
        Assert.False(all.Single(g => g.UserId == withoutKey).UserHasPasskey);

        var matching = await Client.GetFromJsonAsync<List<FunctionGrantDto>>(
            $"/api/function/{fn}/grants?rpId=alerthub.localhost", JsonOptions, ct);
        Assert.True(matching!.Single(g => g.UserId == withKey).UserHasPasskey);

        var foreign = await Client.GetFromJsonAsync<List<FunctionGrantDto>>(
            $"/api/function/{fn}/grants?rpId=other.localhost", JsonOptions, ct);
        Assert.False(foreign!.Single(g => g.UserId == withKey).UserHasPasskey);
    }

    [Fact]
    public async Task A_zero_role_user_gets_403()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Grant", acronym: "ZG", email: "zerogrant@test.com", password: "TestPass1234");
        var zeroClient = await CreateAuthenticatedClientAsync("zg", "TestPass1234");
        var anyId = new ShortGuid(Guid.NewGuid()).ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await zeroClient.GetAsync($"/api/function/{anyId}/grants", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await zeroClient.PostAsJsonAsync($"/api/function/{anyId}/grants", new { UserId = anyId }, JsonOptions, ct)).StatusCode);
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private async Task<string> CreateFunctionAsync(string accountName, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/function", new { AccountName = accountName }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<FunctionPrincipalDto>(JsonOptions, ct))!.Id;
    }

    private async Task<string> CreateUserAsync(string acronym, CancellationToken ct, bool isActive = true)
    {
        var resp = await Client.PostAsJsonAsync("/api/user", new UserCreateDto
        {
            Firstname = "Grant",
            Lastname = acronym.ToUpperInvariant(),
            Acronym = acronym.ToUpperInvariant(),
            Email = $"{acronym}@grant.test",
            IsActive = isActive,
        }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct))!.Id!;
    }

    private async Task<string> IssueAsync(string functionId, string userId, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync($"/api/function/{functionId}/grants", new { UserId = userId }, JsonOptions, ct);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
        return (await resp.Content.ReadFromJsonAsync<FunctionGrantDto>(JsonOptions, ct))!.Id;
    }

    private async Task<FunctionGrantDto> PostTransitionAsync(string functionId, string grantId, string action, CancellationToken ct)
    {
        var resp = await Client.PostAsync($"/api/function/{functionId}/grants/{grantId}/{action}", null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.True(resp.IsSuccessStatusCode, $"{action} failed ({(int)resp.StatusCode}): {body}");
        return (await resp.Content.ReadFromJsonAsync<FunctionGrantDto>(JsonOptions, ct))!;
    }

    private async Task SeedPasskeyAsync(Guid userId, string rpId, CancellationToken ct)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new StoredPasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = RandomNumberGenerator.GetBytes(32),
            PublicKey = RandomNumberGenerator.GetBytes(64),
            UserHandle = userId.ToByteArray(),
            SignatureCount = 0,
            AttestationType = "none",
            DisplayName = "Grant test passkey",
            RpId = rpId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
    }
}
