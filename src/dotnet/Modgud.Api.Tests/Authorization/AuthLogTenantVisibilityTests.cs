using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Authorization;

[Collection(IntegrationTestCollection.Name)]
public class AuthLogTenantVisibilityTests : IntegrationTestBase
{
    public AuthLogTenantVisibilityTests(SharedPostgresFixture fixture) : base(fixture) { }

    private sealed record RealmRow(string ReasonCode);
    private sealed record PlatformRow(string? TargetRealmSlug, string? OperationCode);

    [Fact]
    public async Task ControlPlane_realm_log_reads_only_its_own_physical_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherRealm = ($"log-{Guid.NewGuid():N}")[..12];
        var provisioned = await Factory.Services
            .GetRequiredService<IRealmProvisioningService>()
            .CreateRealmAsync(new CreateRealmDto
            {
                Slug = otherRealm,
                DisplayName = "Log isolation",
                Domains = [$"{otherRealm}.test"],
                InitialAdmin = new InitialAdminDto
                {
                    UserName = "admin",
                    Email = $"admin@{otherRealm}.test",
                },
            }, ct);
        Assert.False(provisioned.IsError);

        await using (var system = GetTenantedDocumentSession("system"))
        {
            system.Store(NewRealmEvent("system-only"));
            await system.SaveChangesAsync(ct);
        }
        await using (var acme = GetTenantedDocumentSession(otherRealm))
        {
            acme.Store(NewRealmEvent("acme-only"));
            await acme.SaveChangesAsync(ct);
        }

        var rows = await Client.GetFromJsonAsync<List<RealmRow>>(
            "/api/admin/auth-log?limit=500", ct);

        Assert.NotNull(rows);
        Assert.Contains(rows!, x => x.ReasonCode == "system-only");
        Assert.DoesNotContain(rows!, x => x.ReasonCode == "acme-only");
    }

    [Fact]
    public async Task Platform_log_is_a_separate_global_store_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = Factory.Services.CreateScope())
        {
            var global = scope.ServiceProvider.GetRequiredService<IGlobalStore>();
            await using var session = global.LightweightSession();
            session.Store(new PlatformAuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEvents.RealmProvisioned,
                Category = AuditEvents.CategoryOf(AuditEvents.RealmProvisioned),
                TargetRealmSlug = "acme",
                OperationCode = "visibility-test",
                OutcomeCode = AuditOutcomes.Succeeded,
            });
            await session.SaveChangesAsync(ct);
        }

        var rows = await Client.GetFromJsonAsync<List<PlatformRow>>(
            "/api/admin/platform-audit?limit=500", ct);

        Assert.NotNull(rows);
        Assert.Contains(rows!, x =>
            x.TargetRealmSlug == "acme" && x.OperationCode == "visibility-test");
    }

    [Fact]
    public async Task ControlPlane_action_keeps_actor_in_actor_realm_and_writes_pii_free_counterpart()
    {
        var ct = TestContext.Current.CancellationToken;
        var targetRealm = ($"cp-audit-{Guid.NewGuid():N}")[..16];
        var response = await Client.PostAsJsonAsync(
            "/api/admin/realms",
            new CreateRealmDto
            {
                Slug = targetRealm,
                DisplayName = "Cross-realm audit",
                Domains = [$"{targetRealm}.test"],
                InitialAdmin = new InitialAdminDto
                {
                    UserName = "admin",
                    Email = $"admin@{targetRealm}.test",
                },
            },
            ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        RealmSecurityAuditEvent? actorEvent = null;
        RealmSecurityAuditEvent? counterpart = null;
        for (var attempt = 0; attempt < 30 &&
             (actorEvent is null || counterpart is null); attempt++)
        {
            await using (var actorRealm = GetTenantedDocumentSession("system"))
            {
                actorEvent = await actorRealm.Query<RealmSecurityAuditEvent>()
                    .FirstOrDefaultAsync(
                        x => x.EventType == AuditEvents.ControlPlaneRealmOperation &&
                             x.TargetRealmSlug == targetRealm &&
                             x.OperationCode == "provision-realm",
                        ct);
            }
            await using (var target = GetTenantedDocumentSession(targetRealm))
            {
                counterpart = await target.Query<RealmSecurityAuditEvent>()
                    .FirstOrDefaultAsync(
                        x => x.EventType == AuditEvents.ControlPlaneRealmOperation &&
                             x.OperationCode == "provision-realm",
                        ct);
            }

            if (actorEvent is null || counterpart is null)
                await Task.Delay(200, ct);
        }

        Assert.NotNull(actorEvent);
        Assert.NotNull(counterpart);
        Assert.Equal(AuditActorKind.User, actorEvent!.ActorKind);
        Assert.NotNull(actorEvent.ActorSubjectId);
        Assert.Equal(targetRealm, actorEvent.TargetRealmSlug);
        Assert.Equal(actorEvent.CorrelationId, counterpart!.CorrelationId);
        Assert.Equal(AuditActorKind.ControlPlane, counterpart.ActorKind);
        Assert.Null(counterpart.ActorSubjectId);
        Assert.Null(counterpart.TargetSubjectId);
        Assert.Null(counterpart.IpAddress);
        Assert.Null(counterpart.UserAgent);
        Assert.Null(counterpart.TargetRealmSlug);
    }

    private static RealmSecurityAuditEvent NewRealmEvent(string reasonCode) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        EventType = AuditEvents.LoginFailedUnknownUser,
        Category = AuditEvents.CategoryOf(AuditEvents.LoginFailedUnknownUser),
        ActorKind = AuditActorKind.AnonymousIdentifier,
        OutcomeCode = AuditOutcomes.Rejected,
        ReasonCode = reasonCode,
    };
}
