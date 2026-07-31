using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

public class RealmAdminInviteEndpointsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Realm_can_be_created_without_admin_and_new_invite_revokes_the_previous_one()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();
        var slug = $"invite-{Guid.NewGuid():N}"[..20];

        try
        {
            var createResponse = await client.PostAsJsonAsync(
                "/api/admin/realms",
                new CreateRealmDto
                {
                    Slug = slug,
                    DisplayName = "Invite Test",
                    Domains = [$"{slug}.localhost"],
                },
                factory.JsonOptions,
                ct);

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var created = await createResponse.Content.ReadFromJsonAsync<CreatedRealmDto>(factory.JsonOptions, ct);
            Assert.NotNull(created);
            Assert.Null(created!.InitialAdminInvite);

            var firstResponse = await client.PostAsJsonAsync(
                $"/api/admin/realms/{slug}/admin-invites",
                new InitialAdminDto { UserName = "first-admin", Email = "first@example.test" },
                factory.JsonOptions,
                ct);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            var first = await firstResponse.Content.ReadFromJsonAsync<InitialAdminInviteDto>(factory.JsonOptions, ct);
            Assert.NotNull(first);

            var secondIssuedAt = DateTimeOffset.UtcNow;
            var secondResponse = await client.PostAsJsonAsync(
                $"/api/admin/realms/{slug}/admin-invites",
                new InitialAdminDto { UserName = "second-admin", Email = "second@example.test" },
                factory.JsonOptions,
                ct);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            var second = await secondResponse.Content.ReadFromJsonAsync<InitialAdminInviteDto>(factory.JsonOptions, ct);
            Assert.NotNull(second);
            Assert.InRange(second!.ExpiresAt,
                secondIssuedAt.AddHours(23).AddMinutes(59),
                secondIssuedAt.AddHours(24).AddMinutes(1));

            using (TenantContext.Enter(slug))
            using (var scope = factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
                var invites = await session.Query<PendingAdminInvite>()
                    .OrderBy(i => i.CreatedAt)
                    .ToListAsync(ct);

                Assert.Equal(2, invites.Count);
                Assert.NotNull(invites[0].UsedAt);
                Assert.Null(invites[1].UsedAt);
                Assert.Equal("second@example.test", invites[1].Email);
                Assert.Single(invites, i => !i.IsUsed);
            }
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/realms/{slug}?hard=true", ct);
        }
    }
}
