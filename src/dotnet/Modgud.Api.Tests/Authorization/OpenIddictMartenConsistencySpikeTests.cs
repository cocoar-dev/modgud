using System.Collections.Immutable;
using System.Security.Claims;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.OpenIddict;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// MG-FT-00 spike 2 — OpenIddict/Marten transaction consistency.
///
/// Pins the two facts the function-terminal staffing grant (MG-FT-05) must be
/// designed around:
///
/// 1. There is NO ambient shared transaction. Every OpenIddict store call opens
///    its own lightweight session via <c>ITenantSessionFactory.OpenSession()</c>
///    and commits immediately — independently of the request-scoped
///    <see cref="IDocumentSession"/> a handler writes its domain documents to.
///    An authorization created through the manager is durable even when the
///    handler's own unit of work is later discarded.
///
/// 2. The compensation path (plan §13.4) therefore is the mechanism for
///    consistency: when a domain write fails AFTER the authorization was
///    created, the handler revokes the just-created authorization via
///    <see cref="IOAuthGrantRevoker.RevokeAuthorizationByIdAsync"/> and returns
///    an error instead of SignIn — so no token is ever minted for it.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OpenIddictMartenConsistencySpikeTests : IntegrationTestBase
{
    public OpenIddictMartenConsistencySpikeTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Authorization_creation_commits_independently_of_the_request_scoped_unit_of_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var nonceId = $"spike-uow-{Guid.NewGuid():N}";
        string? authorizationId;

        using (var scope = Factory.Services.CreateScope())
        {
            var appManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var authManager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var scopedSession = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            var application = await appManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = $"spike-client-{Guid.NewGuid():N}",
                ClientType = ClientTypes.Public,
                DisplayName = "MG-FT-00 spike client",
            }, ct);
            var clientPk = await appManager.GetIdAsync(application, ct);
            Assert.NotNull(clientPk);

            // A pending domain write on the request-scoped session, deliberately
            // never saved — the stand-in for a StaffingSession the handler still
            // has in flight when the authorization gets created.
            scopedSession.Store(new DpopNonceEntry
            {
                Id = nonceId,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });

            var authorization = await authManager.CreateAsync(
                principal: new ClaimsPrincipal(new ClaimsIdentity()),
                subject: DefaultUser!.Id.ToString(),
                client: clientPk!,
                type: AuthorizationTypes.AdHoc,
                scopes: ImmutableArray.Create(Scopes.OpenId), ct);
            authorizationId = await authManager.GetIdAsync(authorization, ct);
            Assert.NotNull(authorizationId);

            // Scope disposed here WITHOUT scopedSession.SaveChangesAsync().
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();

            // The authorization survived: the store committed it on its own
            // session, not on the handler's unit of work.
            var authDoc = await query.LoadAsync<OpenIddictAuthorizationDocument>(authorizationId!, ct);
            Assert.NotNull(authDoc);

            // The handler's pending write did not: the two are separate
            // transactions, so an authorization can exist without its domain
            // counterpart — exactly the gap §13.4 compensates.
            Assert.Null(await query.LoadAsync<DpopNonceEntry>(nonceId, ct));
        }
    }

    [Fact]
    public async Task Failed_domain_write_after_authorization_creation_is_compensated_by_revocation()
    {
        var ct = TestContext.Current.CancellationToken;

        using var scope = Factory.Services.CreateScope();
        var appManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authManager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var revoker = scope.ServiceProvider.GetRequiredService<IOAuthGrantRevoker>();

        var application = await appManager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = $"spike-client-{Guid.NewGuid():N}",
            ClientType = ClientTypes.Public,
            DisplayName = "MG-FT-00 spike client",
        }, ct);
        var clientPk = await appManager.GetIdAsync(application, ct);

        string? authorizationId = null;
        try
        {
            var authorization = await authManager.CreateAsync(
                principal: new ClaimsPrincipal(new ClaimsIdentity()),
                subject: DefaultUser!.Id.ToString(),
                client: clientPk!,
                type: AuthorizationTypes.AdHoc,
                scopes: ImmutableArray.Create(Scopes.OpenId), ct);
            authorizationId = await authManager.GetIdAsync(authorization, ct);

            // The domain write (StaffingSession + terminal update) fails after
            // the authorization is already durable.
            throw new InvalidOperationException("Simulated Marten failure while persisting the StaffingSession.");
        }
        catch (InvalidOperationException)
        {
            // §13.4 compensation: revoke the orphan, return an error, mint nothing.
            Assert.True(await revoker.RevokeAuthorizationByIdAsync(authorizationId!, ct));
        }

        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var authDoc = await query.LoadAsync<OpenIddictAuthorizationDocument>(authorizationId!, ct);
        Assert.NotNull(authDoc);
        Assert.Equal(Statuses.Revoked, authDoc!.Status);

        // No token was ever minted into the revoked family (no SignIn happened),
        // so the terminal cannot act on the failed tap.
        var tokens = await query.Query<OpenIddictTokenDocument>()
            .Where(t => t.AuthorizationId == authorizationId)
            .ToListAsync(ct);
        Assert.Empty(tokens);

        // The compensation primitive fails loudly on a wrong id instead of
        // reporting success — a typo'd compensation must not look like one.
        Assert.False(await revoker.RevokeAuthorizationByIdAsync(Guid.NewGuid().ToString(), ct));
    }
}
