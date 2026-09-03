using Marten;
using Modgud.Infrastructure.Persistence.Tenancy;
using Npgsql;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>
/// Opens counter connections through Marten's tenancy: a realm scope resolves to that
/// realm's tenant database (the same one its documents live in), the global scope to
/// the deployment-wide store. Counters therefore share the realm's isolation and
/// backup/restore boundary and need no separate infrastructure.
/// </summary>
public sealed class MartenRateLimitConnectionSource(IDocumentStore tenantStore, IGlobalStore globalStore) : IRateLimitConnectionSource
{
    public async Task<NpgsqlConnection> OpenAsync(RateLimitScope scope, CancellationToken ct = default)
    {
        NpgsqlConnection connection;
        if (scope.TenantId is { Length: > 0 } tenantId)
        {
            // Same lookup the tenant apply transaction uses; the realm was validated
            // by RealmMiddleware before any rate-limited endpoint runs.
            var database = await tenantStore.Storage.FindOrCreateDatabase(tenantId);
            connection = database.CreateConnection();
        }
        else
        {
            connection = globalStore.Storage.Database.CreateConnection();
        }

        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
