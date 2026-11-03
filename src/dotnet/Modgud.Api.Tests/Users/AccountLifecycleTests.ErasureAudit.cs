using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// A.gdpr-C004 — a permanent erasure, the irreversible GDPR action, wrote no Security-log
/// event of its own: no <c>account.deleted</c> (that comes from a user-stream event the
/// erasure path never appends) and no required streamless record. The docs promise the
/// erasure is traceable in the logs. It now records <c>account.erased</c> with actor,
/// target and the path taken — never the admin's free-text reason.
/// </summary>
public partial class AccountLifecycleTests
{
    [Fact]
    public async Task Admin_permanent_erase_records_who_erased_whom()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Era", lastname: "Sed", acronym: "ERA", email: "erased@test.com");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/users/{new ShortGuid(user.Id)}/permanent")
        {
            Content = JsonContent.Create(new { Reason = "Ticket 4711: requested by phone" }),
        };
        using var erased = await Client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);

        var row = Assert.Single(await ErasureAuditAsync(user.Id));
        Assert.Equal(AuditActorKind.User, row.ActorKind);
        Assert.Equal(DefaultUser!.Id, row.ActorSubjectId);
        Assert.Equal("admin-force-delete", row.ReasonCode);
        Assert.Equal(AuditOutcomes.Succeeded, row.OutcomeCode);
    }

    [Fact]
    public async Task Self_service_sweep_erase_records_a_system_erasure()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Swe", lastname: "Pt", acronym: "SWP", email: "swept@test.com");
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserDeletionState
            {
                Id = user.Id,
                IsDeletionPending = true,
                DeletionInitiator = DeletionInitiator.SelfService,
                DeletionRequestedAt = DateTimeOffset.UtcNow.AddDays(-31),
                DeletionConfirmationDeadline = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await seed.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IGdprService>().RunSelfServiceSweepAsync(ct)).Erased);

        var row = Assert.Single(await ErasureAuditAsync(user.Id));
        Assert.Equal(AuditActorKind.System, row.ActorKind);
        Assert.Null(row.ActorSubjectId);
        Assert.Equal("self-service-grace-expired", row.ReasonCode);
    }

    private async Task<IReadOnlyList<RealmSecurityAuditEvent>> ErasureAuditAsync(Guid userId)
    {
        await using var query = GetTenantedSession();
        return await query.Query<RealmSecurityAuditEvent>()
            .Where(e => e.EventType == AuditEvents.AccountErased && e.TargetSubjectId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
