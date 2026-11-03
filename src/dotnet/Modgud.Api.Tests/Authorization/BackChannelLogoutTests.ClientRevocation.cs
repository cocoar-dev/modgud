using System.Net;
using System.Net.Http.Json;
using Marten;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// E.clients-B3 — the general client admin paths revoked nothing: deleting or disabling
/// a client left its reference access tokens, refresh tokens and authorizations valid in
/// the token store (introspection only checks the token entry, not whether its client
/// still exists or is enabled). The Service-Account credential paths already revoked by
/// application id. Delete and disable now revoke the client's tokens and authorizations;
/// regenerating the secret deliberately does not (product decision 2026-11-03).
/// Placed with these tests for their real auth-code helpers (reference tokens).
/// </summary>
public partial class BackChannelLogoutTests
{
    [Fact]
    public async Task Deleting_a_client_revokes_its_tokens_and_authorizations()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await SignedInRelyingPartyAsync("cldel");
        Assert.NotEmpty(await LiveGrantsAsync(rp.Id));

        using var deleted = await Client.DeleteAsync($"/api/admin/oauth/clients/{rp.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(await LiveGrantsAsync(rp.Id));
    }

    [Fact]
    public async Task Disabling_a_client_revokes_its_tokens_and_authorizations()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await SignedInRelyingPartyAsync("cldis");
        Assert.NotEmpty(await LiveGrantsAsync(rp.Id));

        using var disabled = await Client.PutAsJsonAsync($"/api/admin/oauth/clients/{rp.Id}", new { Enabled = false }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.Empty(await LiveGrantsAsync(rp.Id));
    }

    [Fact]
    public async Task Regenerating_a_client_secret_keeps_its_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var rp = await SignedInRelyingPartyAsync("clsec");
        var before = await LiveGrantsAsync(rp.Id);
        Assert.NotEmpty(before);

        using var rotated = await Client.PostAsync($"/api/admin/oauth/clients/{rp.Id}/regenerate-secret", null, ct);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        Assert.Equal(before.Order(), (await LiveGrantsAsync(rp.Id)).Order());
    }

    /// <summary>A reference-token relying party with one signed-in user's tokens.</summary>
    private async Task<RelyingParty> SignedInRelyingPartyAsync(string tag)
    {
        var (_, userName) = await CreateUserAsync(tag);
        var rp = await CreateRelyingPartyAsync(tag, AccessTokenType.Reference, logoutUri: null);
        await Factory.WaitForProjectionsAsync();
        var cookieClient = await CreateAuthenticatedClientAsync(userName, Password);
        using var tokens = await DriveAuthCodeFlowAsync(cookieClient, rp);
        return rp;
    }

    /// <summary>Ids of the client's still-valid tokens and authorizations.</summary>
    private async Task<List<string>> LiveGrantsAsync(string applicationId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var query = GetTenantedSession();
        var tokens = await query.Query<OpenIddictTokenDocument>()
            .Where(t => t.ApplicationId == applicationId && t.Status == OpenIddictConstants.Statuses.Valid)
            .Select(t => t.Id).ToListAsync(ct);
        var authorizations = await query.Query<OpenIddictAuthorizationDocument>()
            .Where(a => a.ApplicationId == applicationId && a.Status == OpenIddictConstants.Statuses.Valid)
            .Select(a => a.Id).ToListAsync(ct);
        return [.. tokens, .. authorizations];
    }
}
