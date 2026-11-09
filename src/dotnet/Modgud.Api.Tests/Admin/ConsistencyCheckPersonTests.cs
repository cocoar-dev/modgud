using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Admin;

/// <summary>
/// B4-03 — the principal-sync check counted a user's Person as present when the id
/// existed as any principal subtype. A user whose principal row is not a Person is
/// reported as missing its Person.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ConsistencyCheckPersonTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_user_whose_principal_is_not_a_person_is_reported_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Wrong", lastname: "Subtype", acronym: "ws",
            email: "wrong.subtype@test.com", password: "TestPass1234", isRealmAdmin: false);
        await using (var drift = GetTenantedDocumentSession())
        {
            drift.Delete<Person>(user.Id);
            await drift.SaveChangesAsync(ct);
            drift.Store(new ServiceAccount
            {
                Id = user.Id, AccountName = $"drift-{Guid.NewGuid():N}"[..20], Purpose = "consistency probe", IsActive = true,
            });
            await drift.SaveChangesAsync(ct);
        }

        var response = await Client.GetAsync("/api/admin/projections/consistency-check", ct);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var sync = body.RootElement.GetProperty("Checks").EnumerateArray()
            .Single(c => c.GetProperty("Id").GetString() == "principal-sync");
        var missing = sync.GetProperty("Issues").GetProperty("MissingPerson").EnumerateArray()
            .Select(m => m.GetProperty("Id").GetString()).ToList();
        Assert.Contains(missing, id => id == user.Id.ToString() || id == new ShortGuid(user.Id).ToString());
    }
}
