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
            audit.RecordAbuse(new SecurityAuditRecord
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
            audit.RecordAbuse(new SecurityAuditRecord
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
    public async Task Required_event_uses_the_callers_business_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var committedMarker = $"atomic-audit-{Guid.NewGuid():N}";
        var abandonedMarker = $"abandoned-audit-{Guid.NewGuid():N}";
        var audit = Factory.Services.GetRequiredService<ISecurityAuditLog>();

        using (Modgud.Infrastructure.Persistence.Tenancy.TenantContext.Enter("system"))
        {
            await using (var abandoned = GetTenantedDocumentSession("system"))
            {
                audit.StoreRequired(abandoned, new SecurityAuditRecord
                {
                    EventType = AuditEvents.SecurityRetentionChanged,
                    OperationCode = abandonedMarker,
                    RetentionDays = 14,
                    OutcomeCode = AuditOutcomes.Succeeded,
                });
                // Deliberately no SaveChangesAsync: the business transaction
                // is abandoned, therefore its audit row must be abandoned too.
            }

            await using (var committed = GetTenantedDocumentSession("system"))
            {
                audit.StoreRequired(committed, new SecurityAuditRecord
                {
                    EventType = AuditEvents.SecurityRetentionChanged,
                    OperationCode = committedMarker,
                    RetentionDays = 30,
                    OutcomeCode = AuditOutcomes.Succeeded,
                });
                await committed.SaveChangesAsync(ct);
            }
        }

        await using var read = GetTenantedDocumentSession("system");
        Assert.Null(await read.Query<RealmSecurityAuditEvent>()
            .FirstOrDefaultAsync(x => x.OperationCode == abandonedMarker, ct));
        var committedRow = await read.Query<RealmSecurityAuditEvent>()
            .FirstOrDefaultAsync(x => x.OperationCode == committedMarker, ct);
        Assert.NotNull(committedRow);
        Assert.Equal(30, committedRow!.RetentionDays);
    }

    [Fact]
    public async Task Abuse_burst_is_persisted_as_bounded_count_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = $"abuse-aggregate-{Guid.NewGuid():N}";
        var audit = Factory.Services.GetRequiredService<ISecurityAuditLog>();

        using (Modgud.Infrastructure.Persistence.Tenancy.TenantContext.Enter("system"))
        {
            for (var i = 0; i < 3; i++)
            {
                audit.RecordAbuse(new SecurityAuditRecord
                {
                    EventType = AuditEvents.LoginFailedUnknownUser,
                    ActorKind = AuditActorKind.AnonymousIdentifier,
                    UnknownIdentifier = "aggregate@example.test",
                    IpAddress = "203.0.113.80",
                    OutcomeCode = AuditOutcomes.Rejected,
                    ReasonCode = marker,
                });
            }
        }

        IReadOnlyList<RealmSecurityAuditEvent> rows = [];
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await using var read = GetTenantedDocumentSession("system");
            rows = await read.Query<RealmSecurityAuditEvent>()
                .Where(x => x.ReasonCode == marker)
                .ToListAsync(ct);
            if (rows.Sum(x => x.Count ?? 1) >= 3)
                break;
            await Task.Delay(200, ct);
        }

        Assert.Equal(3, rows.Sum(x => x.Count ?? 1));
        Assert.Contains(rows, x => x.Count == 3);
        Assert.All(rows, x =>
        {
            Assert.NotNull(x.FirstObservedAt);
            Assert.NotNull(x.LastObservedAt);
            Assert.True(x.LastObservedAt >= x.FirstObservedAt);
        });
    }

    [Fact]
    public async Task Security_log_has_no_clear_endpoint()
    {
        var response = await Client.DeleteAsync(
            "/api/admin/auth-log", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
