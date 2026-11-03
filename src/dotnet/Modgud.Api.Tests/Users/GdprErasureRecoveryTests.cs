using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Users.Commands;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Principals;
using Npgsql;

namespace Modgud.Api.Tests.Users;

/// <summary>Real Marten commits and injected database failures, never a mocked
/// masking/archiving implementation. Each retry uses a new DI scope.</summary>
public class GdprErasureRecoveryTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("documents", false)]
    [InlineData("masking", false)]
    [InlineData("archive", false)]
    [InlineData("audit", false)]
    [InlineData("completion", false)]
    [InlineData("masking", true)]
    public async Task Failed_step_remains_nonterminal_and_fresh_sweep_resumes(string step, bool selfService)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync("Recover", "Erase", "REC", "recover-erase@test.com");
        var linkId = Guid.NewGuid();
        var historicalLinkId = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        const string sensitive = "erasure-recovery-sentinel";
        var reason = selfService ? "Self-service grace period expired" : "original reason";
        Guid? actor = selfService ? null : DefaultUser!.Id;
        await using (var seed = GetTenantedDocumentSession())
        {
            foreach (var id in new[] { linkId, historicalLinkId })
            {
                seed.Events.StartStream(id, new ExternalIdentityLinkedEvent(id, user.Id, provider,
                    "https://test.invalid", id.ToString(), "recover-erase@test.com", sensitive, DateTimeOffset.UtcNow));
                seed.Events.Append(user.Id, new UserExternalIdentityLinkedEvent(user.Id, id, provider,
                    "https://test.invalid", DateTimeOffset.UtcNow));
            }
            seed.Store(new AuthAuditView { Id = auditId, UserId = user.Id, Ip = "203.0.113.7" });
            if (selfService)
                seed.Store(new UserDeletionState
                {
                    Id = user.Id, IsDeletionPending = true,
                    DeletionInitiator = DeletionInitiator.SelfService,
                    DeletionConfirmationDeadline = DateTimeOffset.UtcNow.AddMinutes(-1),
                });
            await seed.SaveChangesAsync(Ct);
        }
        // A former/unlinked identity has no live projection. Its historical
        // user-stream reference must still enter the durable erasure inventory.
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Delete<ExternalIdentityLink>(historicalLinkId);
            await seed.SaveChangesAsync(Ct);
        }

        await InstallFaultAsync(step, user.Id);
        try
        {
            using var scope = Factory.Services.CreateScope();
            var error = await Record.ExceptionAsync(async () =>
            {
                var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
                if (selfService) await gdpr.RunSelfServiceSweepAsync(Ct);
                else await gdpr.PermanentlyEraseAsync(user.Id, actor, reason, Ct);
            });
            Assert.NotNull(error);
            // Match the deliberate PostgreSQL error, not an arbitrary exception.
            Assert.True(error.ToString().Contains("review_erasure_fault", StringComparison.Ordinal));
        }
        finally
        {
            await RemoveFaultAsync(step);
        }

        await using (var read = GetTenantedSession())
        {
            var pending = await read.LoadAsync<UserDeletionState>(user.Id, Ct);
            Assert.NotNull(pending?.Erasure);
            Assert.False(pending!.IsDataMasked);
            Assert.Null(pending.DataMaskedAt);
            Assert.Equal(reason, pending.Erasure!.Reason);
            Assert.Equal(actor, pending.Erasure.AdminUserId);
            Assert.Contains(linkId, pending.Erasure.ExternalLinkIds);
            Assert.Contains(historicalLinkId, pending.Erasure.ExternalLinkIds);
            Assert.Equal(step != "documents", pending.Erasure.DocumentsCleared);
            var audit = await read.LoadAsync<AuthAuditView>(auditId, Ct);
            Assert.Equal(step == "completion", audit!.Ip is null);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            if (step == "documents")
            {
                var requested = await gdpr.RequestDeletionAsync(user.Id, null, "new request", Ct);
                Assert.True(requested.IsError);
                Assert.Equal("Gdpr.ErasureInProgress", requested.FirstError.Code);
            }
            var cancel = await gdpr.CancelDeletionAsync(user.Id, DefaultUser!.Id, Ct);
            Assert.True(cancel.IsError);
            Assert.Equal("Gdpr.ErasureInProgress", cancel.FirstError.Code);
        }
        using (var scope = Factory.Services.CreateScope())
        {
            var restore = new RestoreUsersHandler(scope.ServiceProvider.GetRequiredService<IDocumentSession>(),
                scope.ServiceProvider.GetRequiredService<IGdprService>());
            var result = await restore.Handle(new RestoreUsersCommand([user.Id], DefaultUser!.Id), Ct);
            Assert.True(result.IsError);
            Assert.Equal("Gdpr.ErasureInProgress", result.FirstError.Code);
        }

        // Manual erasure has no Pending/initiator/deadline. Turning off NEW admin
        // purges must not strand its already irreversible work.
        using (var scope = Factory.Services.CreateScope())
        {
            var policy = await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
                .PatchAsync(new UpdateRealmSettingsDto
                {
                    Deletion = new UpdateDeletionSettingsDto { AutoPurgeEnabled = false },
                }, Ct);
            Assert.False(policy.IsError);
        }
        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            // The other sweep must not steal this work or misclassify its
            // completion counters; in particular AutoPurge=false is not a kill
            // switch for existing irreversible work.
            if (selfService) Assert.Equal(0, await gdpr.RunAdminRetentionPurgeAsync(Ct));
            else Assert.Equal(0, (await gdpr.RunSelfServiceSweepAsync(Ct)).Erased);
            if (step == "completion")
            {
                var resumed = await gdpr.PermanentlyEraseAsync(user.Id, null, "replacement reason", Ct);
                Assert.False(resumed.IsError);
            }
            else
            {
                var erased = selfService
                    ? (await gdpr.RunSelfServiceSweepAsync(Ct)).Erased
                    : await gdpr.RunAdminRetentionPurgeAsync(Ct);
                Assert.Equal(1, erased);
            }
        }
        await using (var read = GetTenantedSession())
        {
            var completed = await read.LoadAsync<UserDeletionState>(user.Id, Ct);
            Assert.True(completed!.IsDataMasked);
            Assert.False(completed.IsDeletionPending);
            Assert.Null(completed.Erasure);
            Assert.NotNull(completed.DataMaskedAt);
            Assert.Equal(reason, completed.DataMaskedReason);
            Assert.Equal(actor, completed.DataMaskedByUserId);
            Assert.True((await read.LoadAsync<ApplicationUser>(user.Id, Ct))!.IsDeleted);
            Assert.Null(await read.LoadAsync<Principal>(user.Id, Ct));
            Assert.Null(await read.LoadAsync<UserSecurityData>(user.Id, Ct));
            Assert.Null(await read.LoadAsync<ExternalIdentityLink>(linkId, Ct));
            Assert.Null((await read.LoadAsync<AuthAuditView>(auditId, Ct))!.Ip);
            foreach (var id in new[] { user.Id, linkId, historicalLinkId })
            {
                var schema = Quote(read.DocumentStore.Options.Events.DatabaseSchemaName);
                var raw = await read.QueryAsync<string>($"select data::text from {schema}.mt_events where stream_id = '{id}'", Ct);
                Assert.NotEmpty(raw);
                Assert.True(raw.All(json => !json.Contains(sensitive, StringComparison.Ordinal)));
                var unarchived = await read.QueryAsync<int>($"select count(*)::int from {schema}.mt_events where stream_id = '{id}' and not is_archived", Ct);
                Assert.Equal(0, unarchived.Single());
            }
        }
        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            Assert.Equal(0, (await gdpr.RunSelfServiceSweepAsync(Ct)).Erased);
            var repeated = await gdpr.PermanentlyEraseAsync(user.Id, null, "replacement reason", Ct);
            Assert.True(repeated.IsError);
            Assert.Equal("Gdpr.AlreadyMasked", repeated.FirstError.Code);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_and_erasure_intent_cannot_overwrite_each_other(bool cancellationWins)
    {
        var id = Guid.NewGuid();
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserDeletionState { Id = id, IsDeletionPending = true, DeletionInitiator = DeletionInitiator.SelfService });
            await seed.SaveChangesAsync(Ct);
        }
        await using var cancel = GetTenantedDocumentSession();
        await using var erase = GetTenantedDocumentSession();
        var cancelled = (await cancel.LoadAsync<UserDeletionState>(id, Ct))!;
        // Sweeps materialize query results rather than LoadAsync: verify those
        // carry the same optimistic concurrency protection.
        var claimed = (await erase.Query<UserDeletionState>().Where(s => s.Id == id).ToListAsync(Ct)).Single();
        cancelled.IsDeletionPending = false;
        claimed.Erasure = new UserErasureProgress { StartedAt = DateTimeOffset.UtcNow, Reason = "race" };
        cancel.Store(cancelled);
        erase.Store(claimed);
        var winner = cancellationWins ? cancel : erase;
        var loser = cancellationWins ? erase : cancel;
        await winner.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ConcurrencyException>(() => loser.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Concurrent_erase_and_sweeps_are_rejected_until_failed_or_cancelled_owner_exits(
        bool resume, bool cancel)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync("Lock", "Erase", "LCK", "lock-erase@test.com");
        if (resume)
        {
            await using var seed = GetTenantedDocumentSession();
            seed.Store(new UserDeletionState
            {
                Id = user.Id,
                Erasure = new UserErasureProgress { StartedAt = DateTimeOffset.UtcNow, Reason = "original intent" },
            });
            await seed.SaveChangesAsync(Ct);
        }

        using var owner = Factory.Services.CreateScope();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var paused = new PausingRevoker();
        // Only pause the real service at the first irreversible helper boundary;
        // all locking and durable state writes use the actual database.
        var service = ActivatorUtilities.CreateInstance<GdprService>(owner.ServiceProvider, paused);
        var running = service.PermanentlyEraseAsync(user.Id, null, "new intent", cancelled.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            using var contender = Factory.Services.CreateScope();
            var gdpr = contender.ServiceProvider.GetRequiredService<IGdprService>();
            var busy = await gdpr.PermanentlyEraseAsync(user.Id, null, "competing intent", Ct);
            Assert.True(busy.IsError);
            Assert.Equal("Gdpr.ErasureBusy", busy.FirstError.Code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gdpr.RunSelfServiceSweepAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => gdpr.RunAdminRetentionPurgeAsync(Ct));
            await using var read = GetTenantedSession();
            var pending = (await read.LoadAsync<UserDeletionState>(user.Id, Ct))!;
            Assert.False(pending.IsDataMasked);
            Assert.False(pending.Erasure!.DocumentsCleared);
            Assert.Equal(resume ? "original intent" : "new intent", pending.Erasure.Reason);
        }
        finally
        {
            if (cancel) await cancelled.CancelAsync();
            paused.Release.TrySetResult();
            var failure = await Record.ExceptionAsync(async () => await running);
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else Assert.IsType<InvalidOperationException>(failure);
        }

        // Explicit unlock must work even when the owner's token was cancelled.
        using var retry = Factory.Services.CreateScope();
        var result = await retry.ServiceProvider.GetRequiredService<IGdprService>()
            .PermanentlyEraseAsync(user.Id, null, "replacement intent", Ct);
        Assert.False(result.IsError);
        await using var completed = GetTenantedSession();
        var state = (await completed.LoadAsync<UserDeletionState>(user.Id, Ct))!;
        Assert.True(state.IsDataMasked);
        Assert.Null(state.Erasure);
        Assert.Equal(resume ? "original intent" : "new intent", state.DataMaskedReason);
    }

    private sealed class PausingRevoker : IUserAccessRevoker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RevokeAllAccessAsync(Guid userId, AccessRevocationReason reason, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            throw new InvalidOperationException("review_revoker_fault");
        }
    }

    private static string Quote(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);

    private async Task InstallFaultAsync(string step, Guid id)
    {
        await using var session = GetTenantedDocumentSession();
        var (table, condition) = Fault(step, id);
        var schema = Quote(session.DocumentStore.Options.DatabaseSchemaName);
        if (step is "masking" or "archive") schema = Quote(session.DocumentStore.Options.Events.DatabaseSchemaName);
        // All identifiers/conditions are constants or test-generated GUIDs.
        // The fixture has exclusively created this development/test database.
        var sql = $"""
            CREATE FUNCTION {schema}.review_erasure_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF {condition} THEN RAISE EXCEPTION 'review_erasure_fault'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER review_erasure_fault BEFORE INSERT OR UPDATE ON {schema}.{table}
                FOR EACH ROW EXECUTE FUNCTION {schema}.review_erasure_fault();
            """;
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand(sql);
#pragma warning restore CA2100
        await session.ExecuteAsync(command, Ct);
    }

    private async Task RemoveFaultAsync(string step)
    {
        await using var session = GetTenantedDocumentSession();
        var schema = Quote(step is "masking" or "archive"
            ? session.DocumentStore.Options.Events.DatabaseSchemaName : session.DocumentStore.Options.DatabaseSchemaName);
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand($"DROP FUNCTION {schema}.review_erasure_fault() CASCADE");
#pragma warning restore CA2100
        await session.ExecuteAsync(command, Ct);
    }

    private static (string Table, string Condition) Fault(string step, Guid id) => step switch
    {
        "documents" => ("mt_doc_applicationuser", $"NEW.id = '{id}'::uuid AND (NEW.data->>'IsDeleted')::boolean = true"),
        "masking" => ("mt_events", $"TG_OP = 'UPDATE' AND NEW.stream_id = '{id}'::uuid AND NEW.data IS DISTINCT FROM OLD.data"),
        "archive" => ("mt_events", $"NEW.stream_id = '{id}'::uuid AND NEW.is_archived = true"),
        "audit" => ("mt_doc_auth_audit_view", $"NEW.data->>'UserId' = '{id}' AND NEW.data->>'Ip' IS NULL"),
        "completion" => ("mt_doc_userdeletionstate", $"NEW.id = '{id}'::uuid AND (NEW.data->>'IsDataMasked')::boolean = true"),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };
}
