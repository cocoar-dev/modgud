using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Gdpr;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 4 of the "similar bugs" remediation — findings #20/#21: GDPR permanent erase
/// left PII in the queryable projection read-models. Marten event-masking rewrites
/// event JSON only (no projection re-run), and the UserDeletedEvent Apply handlers
/// merely flag IsDeleted while keeping Email/name — so the "forgotten" user's name +
/// email survived in the inline Principal/Person doc (also served by
/// /api/principal/lookup) and the async UserView, and the Person stayed IsActive=true.
/// The fix hard-deletes both projection docs during the erase.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave4Tests : IntegrationTestBase
{
    public SecurityAuditWave4Tests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PermanentErase_RemovesPiiFromPrincipalAndUserViewProjections()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Erase", lastname: "Me", acronym: "EM", email: "erase-me@test.com", password: "TestPass1234");

        // Sanity: the PII is present in the projections before erase.
        await using (var qs = GetTenantedSession())
        {
            var p = await qs.LoadAsync<Person>(user.Id, ct);
            Assert.NotNull(p);
            Assert.Equal("erase-me@test.com", p!.Email);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var r = await gdpr.PermanentlyEraseAsync(user.Id, adminUserId: null, reason: "wave4-test", ct);
            Assert.False(r.IsError, r.IsError ? r.FirstError.Description : null);
        }

        // After erase: no PII-bearing projection doc remains, and the user is not
        // discoverable by email. RED today: Person + UserView survive with real PII.
        await using (var qs = GetTenantedSession())
        {
            Assert.Null(await qs.LoadAsync<Person>(user.Id, ct));
            Assert.Null(await qs.LoadAsync<UserView>(user.Id, ct));
            var byEmail = await qs.Query<Person>()
                .Where(p => p.NormalizedEmail == "ERASE-ME@TEST.COM")
                .ToListAsync(ct);
            Assert.Empty(byEmail);
        }
    }
}
