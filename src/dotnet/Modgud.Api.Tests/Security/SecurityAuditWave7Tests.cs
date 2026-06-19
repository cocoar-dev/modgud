using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 7 of the "similar bugs" remediation — authz/service-account revocation
/// (#19, #6, #7, #8). A security-state change on a service account (deactivate /
/// delete / credential rotate) must cut off its live M2M tokens, and the
/// cross-type principal lookup must not leak directory PII to any caller.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave7Tests : IntegrationTestBase
{
    private const string Password = "TestPass1234";

    public SecurityAuditWave7Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #19 — /api/principal/lookup must require a permission. A zero-role authenticated
    // user could previously enumerate the whole realm directory (+ emails).
    [Fact]
    public async Task PrincipalLookup_WithoutPermission_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Zero", lastname: "Role", acronym: "ZR", email: "zero@test.com", password: Password);
        var zeroClient = await CreateAuthenticatedClientAsync("zr", Password);

        var resp = await zeroClient.GetAsync("/api/principal/lookup", ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // #19 — and even for an authorized caller, Email is no longer in the projection.
    [Fact]
    public async Task PrincipalLookup_DoesNotLeakEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await Client.GetAsync("/api/principal/lookup", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("\"Email\"", body);
        Assert.DoesNotContain("test@test.com", body);
    }

    // #6 — deactivating a service account must revoke its live M2M tokens (sub = sa.Id).
    [Fact]
    public async Task DeactivateServiceAccount_RevokesItsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saIdShort, saId) = await CreateServiceAccountAsync("svc-deact", ct);
        var tokenId = await SeedTokenAsync(subject: saId.ToString(), applicationId: "client-x", ct);

        var resp = await Client.PutAsJsonAsync($"/api/service-account/{saIdShort}", new { IsActive = false }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await AssertTokenRevokedAsync(tokenId, ct);
    }

    // #7 — deleting a service account must revoke its live M2M tokens.
    [Fact]
    public async Task DeleteServiceAccount_RevokesItsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saIdShort, saId) = await CreateServiceAccountAsync("svc-del", ct);
        var tokenId = await SeedTokenAsync(subject: saId.ToString(), applicationId: "client-y", ct);

        var resp = await Client.DeleteAsync($"/api/service-account/{saIdShort}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await AssertTokenRevokedAsync(tokenId, ct);
    }

    // #8 — rotating a credential's secret must revoke exactly that client's tokens.
    [Fact]
    public async Task RotateServiceAccountCredential_RevokesThatClientsTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (saIdShort, _) = await CreateServiceAccountAsync("svc-rot", ct);

        // Issue a real credential (a confidential client_credentials client).
        var issueResp = await Client.PostAsJsonAsync(
            $"/api/service-account/{saIdShort}/credentials", new { DisplayName = "cred" }, ct);
        Assert.Equal(HttpStatusCode.OK, issueResp.StatusCode);
        var issued = await issueResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        var credId = issued.GetProperty("Credential").GetProperty("Id").GetString()!;

        // A token minted for that client carries ApplicationId == credId.
        var tokenId = await SeedTokenAsync(subject: Guid.NewGuid().ToString(), applicationId: credId, ct);

        var rotateResp = await Client.PostAsync($"/api/service-account/{saIdShort}/credentials/{credId}/rotate", null, ct);
        Assert.Equal(HttpStatusCode.OK, rotateResp.StatusCode);

        await AssertTokenRevokedAsync(tokenId, ct);
    }

    private async Task<(string Short, Guid Guid)> CreateServiceAccountAsync(string name, CancellationToken ct)
    {
        var resp = await Client.PostAsJsonAsync("/api/service-account",
            new { AccountName = name, Purpose = "wave7" }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        var shortId = dto.GetProperty("Id").GetString()!;
        return (shortId, new ShortGuid(shortId).Guid);
    }

    private async Task<string> SeedTokenAsync(string subject, string applicationId, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        await using var session = GetTenantedDocumentSession();
        session.Store(new OpenIddictTokenDocument
        {
            Id = id,
            Subject = subject,
            ApplicationId = applicationId,
            Status = OpenIddictConstants.Statuses.Valid,
            Type = OpenIddictConstants.TokenTypeHints.AccessToken,
            CreationDate = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
        return id;
    }

    private async Task AssertTokenRevokedAsync(string tokenId, CancellationToken ct)
    {
        await using var session = GetTenantedDocumentSession();
        var token = await session.LoadAsync<OpenIddictTokenDocument>(tokenId, ct);
        Assert.NotNull(token);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, token!.Status);
    }
}
