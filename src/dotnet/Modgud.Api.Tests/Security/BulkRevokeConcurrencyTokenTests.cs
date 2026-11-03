using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.OAuth.Storage;
using OpenIddict.Abstractions;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B4-01 — the bulk revoke of the OpenIddict token and authorization stores flipped the
/// status to Revoked without rotating the cross-session <c>ConcurrencyToken</c>. A caller
/// that loaded the row before the revoke then passed the store's stale-write check in
/// <c>UpdateAsync</c> (same token) and overwrote Revoked — for a refresh token, with
/// Redeemed, after which OpenIddict mints a new pair. Deterministic, as in Wave 5: the
/// stale copy is loaded before the revoke and written after it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class BulkRevokeConcurrencyTokenTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_token_loaded_before_a_bulk_revoke_cannot_overwrite_it()
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenStore<OpenIddictTokenDocument>>();
        var authorizationId = Guid.NewGuid().ToString();
        var token = new OpenIddictTokenDocument
        {
            Id = Guid.NewGuid().ToString(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = Guid.NewGuid().ToString(),
            AuthorizationId = authorizationId,
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            CreationDate = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(token, Ct);

        var stale = await store.FindByIdAsync(token.Id, Ct);
        Assert.Equal(1, await store.RevokeByAuthorizationIdAsync(authorizationId, Ct));

        // The redeem that loaded the token before the revoke now persists its decision.
        await store.SetStatusAsync(stale!, OpenIddictConstants.Statuses.Redeemed, Ct);
        await Assert.ThrowsAsync<OpenIddictExceptions.ConcurrencyException>(
            async () => await store.UpdateAsync(stale!, Ct));

        var final = await store.FindByIdAsync(token.Id, Ct);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, final!.Status);
    }

    [Fact]
    public async Task An_authorization_loaded_before_a_bulk_revoke_cannot_overwrite_it()
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IOpenIddictAuthorizationStore<OpenIddictAuthorizationDocument>>();
        var subject = Guid.NewGuid().ToString();
        var authorization = new OpenIddictAuthorizationDocument
        {
            Id = Guid.NewGuid().ToString(),
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = subject,
            Type = OpenIddictConstants.AuthorizationTypes.Permanent,
            CreationDate = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(authorization, Ct);

        var stale = await store.FindByIdAsync(authorization.Id, Ct);
        Assert.Equal(1, await store.RevokeBySubjectAsync(subject, Ct));

        await Assert.ThrowsAsync<OpenIddictExceptions.ConcurrencyException>(
            async () => await store.UpdateAsync(stale!, Ct));

        var final = await store.FindByIdAsync(authorization.Id, Ct);
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, final!.Status);
    }
}
