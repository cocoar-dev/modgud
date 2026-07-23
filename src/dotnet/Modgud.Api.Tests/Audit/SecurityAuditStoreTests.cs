using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Audit;

[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditStoreTests : IntegrationTestBase
{
    public SecurityAuditStoreTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Structured_forensic_record_survives_subject_erasure_until_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Boundary", "Victim", "bv", "boundary-victim@acme.com");
        var rowId = Guid.NewGuid();

        await using (var write = GetTenantedDocumentSession("system"))
        {
            write.Store(new RealmSecurityAuditEvent
            {
                Id = rowId,
                Timestamp = DateTimeOffset.UtcNow,
                Severity = AuditSeverity.Warning,
                EventType = AuditEvents.LoginFailed,
                Category = AuditEvents.CategoryOf(AuditEvents.LoginFailed),
                ActorKind = AuditActorKind.User,
                TargetSubjectId = user.Id,
                IpAddress = "203.0.113.50",
                OutcomeCode = AuditOutcomes.Rejected,
                ReasonCode = "invalid-credentials",
            });
            await write.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var result = await gdpr.PermanentlyEraseAsync(
                user.Id, adminUserId: null, reason: "security-retention-test", ct);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        }

        await using var read = GetTenantedDocumentSession("system");
        var survived = await read.LoadAsync<RealmSecurityAuditEvent>(rowId, ct);
        Assert.NotNull(survived);
        Assert.Equal(user.Id, survived!.TargetSubjectId);
        Assert.Equal("203.0.113.50", survived.IpAddress);
    }

    [Fact]
    public async Task Unknown_identifier_is_persisted_only_as_realm_hmac()
    {
        var ct = TestContext.Current.CancellationToken;
        const string rawIdentifier = "Unknown.Person@Example.test";
        var marker = $"hmac-test-{Guid.NewGuid():N}";
        var otherRealm = ($"hmac-{Guid.NewGuid():N}")[..13];
        var provisioned = await Factory.Services
            .GetRequiredService<IRealmProvisioningService>()
            .CreateRealmAsync(new CreateRealmDto
            {
                Slug = otherRealm,
                DisplayName = "HMAC isolation",
                Domains = [$"{otherRealm}.test"],
                InitialAdmin = new InitialAdminDto
                {
                    UserName = "admin",
                    Email = $"admin@{otherRealm}.test",
                },
            }, ct);
        Assert.False(provisioned.IsError);
        var audit = Factory.Services.GetRequiredService<ISecurityAuditLog>();

        using (Modgud.Infrastructure.Persistence.Tenancy.TenantContext.Enter("system"))
        {
            audit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.LoginFailedUnknownUser,
                ActorKind = AuditActorKind.AnonymousIdentifier,
                UnknownIdentifier = rawIdentifier,
                OutcomeCode = AuditOutcomes.Rejected,
                ReasonCode = marker,
            });
        }
        using (Modgud.Infrastructure.Persistence.Tenancy.TenantContext.Enter(otherRealm))
        {
            audit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.LoginFailedUnknownUser,
                ActorKind = AuditActorKind.AnonymousIdentifier,
                UnknownIdentifier = rawIdentifier,
                OutcomeCode = AuditOutcomes.Rejected,
                ReasonCode = marker,
            });
        }

        RealmSecurityAuditEvent? systemRecorded = null;
        RealmSecurityAuditEvent? acmeRecorded = null;
        for (var attempt = 0; attempt < 25 &&
             (systemRecorded is null || acmeRecorded is null); attempt++)
        {
            await using (var system = GetTenantedDocumentSession("system"))
            {
                systemRecorded = await system.Query<RealmSecurityAuditEvent>()
                    .FirstOrDefaultAsync(x => x.ReasonCode == marker, ct);
            }
            await using (var acme = GetTenantedDocumentSession(otherRealm))
            {
                acmeRecorded = await acme.Query<RealmSecurityAuditEvent>()
                    .FirstOrDefaultAsync(x => x.ReasonCode == marker, ct);
            }
            if (systemRecorded is null || acmeRecorded is null)
                await Task.Delay(200, ct);
        }

        Assert.NotNull(systemRecorded);
        Assert.NotNull(acmeRecorded);
        Assert.Matches("^[0-9a-f]{64}$", systemRecorded!.UnknownIdentifierFingerprint);
        Assert.DoesNotContain(
            rawIdentifier,
            systemRecorded.UnknownIdentifierFingerprint!,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            systemRecorded.UnknownIdentifierFingerprint,
            acmeRecorded!.UnknownIdentifierFingerprint);
    }

    [Fact]
    public async Task Security_log_has_no_clear_endpoint()
    {
        var response = await Client.DeleteAsync(
            "/api/admin/auth-log", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
