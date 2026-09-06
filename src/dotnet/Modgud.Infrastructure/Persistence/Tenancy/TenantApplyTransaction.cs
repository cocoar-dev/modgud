using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Modgud.Infrastructure.Persistence.Tenancy;

/// <summary>
/// The ambient transaction behind a declarative manifest apply (ADR-0017 Phase 0):
/// ONE Npgsql transaction on the target tenant's database that every Marten session
/// opened inside the apply scope enlists in, plus a collector for consequence
/// actions that must only run AFTER the transaction committed.
///
/// <para>Usage (see <c>RealmManifestApplier</c>): <c>BeginAsync</c> opens the
/// connection + transaction, <see cref="Activate"/> installs the ambient marker
/// SYNCHRONOUSLY in the caller's execution context (an async method cannot
/// propagate an AsyncLocal up to its caller — the same reason
/// <see cref="TenantContext.Enter"/> is synchronous). While active,
/// <see cref="TenantedSessionFactory"/> binds every tenant session to this
/// transaction with <c>shouldAutoCommit: false</c>, so each canonical operation's
/// <c>SaveChangesAsync</c> flushes its commands into the shared transaction without
/// committing it — reads on the same connection see those writes, which keeps
/// in-op validations (name-taken checks, reference resolution) correct.</para>
///
/// <para>Consequence actions (token revocation, staffing-session termination —
/// see the <c>Deferring*</c> decorators) are recorded via <see cref="Defer"/> and
/// executed by <see cref="RunDeferredAsync"/> after <see cref="CommitAsync"/>,
/// each in a fresh DI scope with the ambient marker gone, so they observe
/// committed state and use ordinary self-committing sessions. If the apply fails,
/// disposing rolls the transaction back and the recorded consequences are
/// discarded — no action ever runs for a change that never happened.</para>
/// </summary>
public sealed class TenantApplyTransaction : IAsyncDisposable
{
    private static readonly AsyncLocal<TenantApplyTransaction?> Ambient = new();

    /// <summary>The apply transaction active on the current execution flow, if any.</summary>
    public static TenantApplyTransaction? Current => Ambient.Value;

    private readonly List<(string What, Func<IServiceProvider, CancellationToken, Task> Action)> _deferred = [];
    private bool _committed;

    public string TenantId { get; }
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    private TenantApplyTransaction(string tenantId, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        TenantId = tenantId;
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>Opens a connection to the tenant's database and begins the shared
    /// transaction. Does NOT install the ambient marker — call <see cref="Activate"/>
    /// from the consuming (synchronous) frame.</summary>
    public static async Task<TenantApplyTransaction> BeginAsync(
        IDocumentStore store, string tenantId, CancellationToken ct = default)
    {
        if (Ambient.Value is not null)
            throw new InvalidOperationException("A tenant apply transaction is already active; nesting is not supported.");

        var database = await store.Storage.FindOrCreateDatabase(tenantId);
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(ct);
            return new TenantApplyTransaction(tenantId, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Installs this transaction as the ambient one. Synchronous on purpose
    /// (AsyncLocal writes only flow DOWN); dispose the returned scope before running
    /// the deferred consequences.</summary>
    public IDisposable Activate()
    {
        if (Ambient.Value is not null)
            throw new InvalidOperationException("A tenant apply transaction is already active; nesting is not supported.");
        Ambient.Value = this;
        return new AmbientScope();
    }

    private sealed class AmbientScope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }

    /// <summary>Session options binding a Marten session to the shared transaction.
    /// <c>shouldAutoCommit: false</c> — SaveChanges flushes but never commits.</summary>
    public SessionOptions CreateSessionOptions()
    {
        var options = SessionOptions.ForTransaction(Transaction, shouldAutoCommit: false);
        options.TenantId = TenantId;
        return options;
    }

    /// <summary>Records a consequence action to run after a successful commit. The
    /// action receives a FRESH scoped service provider (ambient marker gone), so it
    /// must re-resolve its services — never capture scoped services from the apply.</summary>
    public void Defer(string what, Func<IServiceProvider, CancellationToken, Task> action)
        => _deferred.Add((what, action));

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await Transaction.CommitAsync(ct);
        _committed = true;
    }

    /// <summary>Runs the recorded consequences, each in its own DI scope. Failures
    /// are logged and do not fail the (already committed) apply — the actions are
    /// idempotent/retryable, same failure class as any single admin operation.</summary>
    public async Task RunDeferredAsync(
        IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken ct = default)
    {
        if (!_committed)
            throw new InvalidOperationException("Deferred apply consequences must not run before the transaction committed.");
        if (Ambient.Value is not null)
            throw new InvalidOperationException("Deactivate the ambient apply transaction before running deferred consequences.");

        foreach (var (what, action) in _deferred)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await action(scope.ServiceProvider, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Deferred apply consequence '{What}' failed after commit; the configuration change itself is persisted. The action is idempotent — re-applying or the next admin operation on the entity retries it.",
                    what);
            }
        }
        _deferred.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = null;
        if (!_committed)
        {
            try { await Transaction.RollbackAsync(); }
            catch
            {
                // Connection teardown below discards the transaction anyway; a failed
                // explicit rollback (e.g. broken connection) must not mask the original
                // apply error.
            }
        }
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
