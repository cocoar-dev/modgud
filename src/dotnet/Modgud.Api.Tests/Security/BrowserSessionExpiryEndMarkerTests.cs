using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// CS01 — a browser session found expired on validation (a request or a live hub
/// connection reaching it before the retention job) was deleted without the ADR 0021
/// end marker: its relying-party grants stayed behind and were later swept without an
/// end event, so the RPs were never told. Expiry on validation now ends the session the
/// same way the retention prune does.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class BrowserSessionExpiryEndMarkerTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expiry_found_on_validation_ends_the_relying_party_sessions()
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var browserSession = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now.AddHours(-3),
            LastActiveAt = now.AddHours(-2),
            ExpiresAt = now.AddMinutes(-1),
            AbsoluteExpiresAt = now.AddDays(1),
        };
        const string clientId = "cs01-rp";
        await using (var arrange = GetTenantedDocumentSession())
        {
            arrange.Store(browserSession);
            arrange.Store(new SessionGrant
            {
                Id = SessionGrant.IdFor(browserSession.Id, clientId),
                SessionId = browserSession.Id,
                UserId = userId,
                ClientId = clientId,
                Kind = AccessSessionKind.Browser,
                Issuer = "https://localhost/",
                FirstIssuedAt = now.AddHours(-2),
                LastIssuedAt = now.AddHours(-2),
            });
            await arrange.SaveChangesAsync(Ct);
        }

        using (var scope = Factory.Services.CreateScope())
        using (TenantContext.Enter(TenantConstants.SystemTenantId))
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            Assert.Null(await sessions.ValidateSessionAsync(userId, browserSession.Id, touch: true, Ct));
        }

        await using var verify = GetTenantedSession();
        Assert.Null(await verify.LoadAsync<UserSession>(browserSession.Id, Ct));
        Assert.Empty(await verify.Query<SessionGrant>().Where(g => g.SessionId == browserSession.Id).ToListAsync(Ct));
        var ended = (await verify.Events.FetchStreamAsync(userId, token: Ct))
            .Select(e => e.Data).OfType<UserAccessEndedEvent>().ToList();
        var marker = Assert.Single(ended);
        Assert.Equal(AccessEndScope.Session, marker.Scope);
        Assert.Equal(browserSession.Id, marker.SessionId);
        Assert.Equal(AccessEndReasons.Expired, marker.Reason);
        Assert.Contains(marker.Targets, t => t.ClientId == clientId);
    }
}
