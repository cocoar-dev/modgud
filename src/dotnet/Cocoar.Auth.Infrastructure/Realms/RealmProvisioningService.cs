using System.Text.RegularExpressions;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Authorization;
using Cocoar.Auth.Infrastructure.OAuth;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cocoar.Auth.Infrastructure.Realms;

/// <summary>
/// Service for managing realm lifecycle: creation, updates, soft-delete, and DB
/// provisioning. Realm metadata lives in the master DB (<see cref="IGlobalStore"/>).
/// Tenant DBs are PostgreSQL databases named <c>{mainDb}_{slug}</c>.
/// Raw SQL is only used for <c>CREATE DATABASE</c> (DDL — not supported by Marten).
/// </summary>
public interface IRealmProvisioningService
{
    Task<List<Realm>> GetAllRealmsAsync(CancellationToken ct = default);
    Task<Realm?> GetRealmBySlugAsync(string slug, CancellationToken ct = default);
    Task<ErrorOr<Realm>> CreateRealmAsync(CreateRealmDto dto, CancellationToken ct = default);
    Task<ErrorOr<Realm>> UpdateRealmAsync(string slug, UpdateRealmDto dto, CancellationToken ct = default);
    Task<ErrorOr<bool>> DeleteRealmAsync(string slug, CancellationToken ct = default);
    Task EnsureSystemRealmExistsAsync(CancellationToken ct = default);
}

public sealed class RealmProvisioningService : IRealmProvisioningService
{
    private readonly IGlobalStore _globalStore;
    private readonly IDocumentStore _tenantedStore;
    private readonly IMasterConnectionString _masterCs;
    private readonly IRealmCache _realmCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RealmProvisioningService> _logger;

    public RealmProvisioningService(
        IGlobalStore globalStore,
        IDocumentStore tenantedStore,
        IMasterConnectionString masterCs,
        IRealmCache realmCache,
        IServiceProvider serviceProvider,
        ILogger<RealmProvisioningService> logger)
    {
        _globalStore = globalStore;
        _tenantedStore = tenantedStore;
        _masterCs = masterCs;
        _realmCache = realmCache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<List<Realm>> GetAllRealmsAsync(CancellationToken ct = default)
    {
        await using var session = _globalStore.QuerySession();
        var realms = await session.Query<Realm>()
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        return realms.ToList();
    }

    public async Task<Realm?> GetRealmBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var session = _globalStore.QuerySession();
        return await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);
    }

    public async Task<ErrorOr<Realm>> CreateRealmAsync(CreateRealmDto dto, CancellationToken ct = default)
    {
        if (!RealmSlugRules.IsValidFormat(dto.Slug))
        {
            return Error.Validation("Realm.InvalidSlug",
                "Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens.");
        }

        if (RealmSlugRules.IsReserved(dto.Slug))
        {
            return Error.Validation("Realm.ReservedSlug",
                $"The slug '{dto.Slug}' is reserved and cannot be used.");
        }

        await using var session = _globalStore.LightweightSession();
        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == dto.Slug, ct);
        if (existing is not null)
        {
            return Error.Conflict("Realm.DuplicateSlug",
                $"A realm with slug '{dto.Slug}' already exists.");
        }

        // Build the tenant database connection string
        var csBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value);
        var mainDbName = csBuilder.Database!;
        var tenantDbName = $"{mainDbName}_{dto.Slug}";
        csBuilder.Database = tenantDbName;
        var tenantCs = csBuilder.ConnectionString;

        // Raw SQL: create the PostgreSQL database (DDL — cannot use Marten/parameters)
        var bootstrapBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value) { Database = "postgres" };
        await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
        {
            await bootstrapConn.OpenAsync(ct);
            await using var checkDbCmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @dbName", bootstrapConn);
            checkDbCmd.Parameters.AddWithValue("@dbName", tenantDbName);
            if (await checkDbCmd.ExecuteScalarAsync(ct) is null)
            {
                var quotedName = "\"" + tenantDbName.Replace("\"", "\"\"") + "\"";
                await using var createDbCmd = new NpgsqlCommand(
                    $"CREATE DATABASE {quotedName}", bootstrapConn);
                await createDbCmd.ExecuteNonQueryAsync(ct);
                _logger.LogInformation("Created database {DbName} for realm {Slug}", tenantDbName, dto.Slug);
            }
        }

        // Register in Marten's tenant registry
        var tenancy = (Marten.Storage.MasterTableTenancy)_tenantedStore.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync(dto.Slug, tenantCs);

        // Apply Marten schema to the new tenant database (tables, functions, indexes)
        var newTenantDb = await tenancy.FindOrCreateDatabase(dto.Slug);
        await newTenantDb.ApplyAllConfiguredChangesToDatabaseAsync();

        var domains = dto.Domains is { Length: > 0 }
            ? dto.Domains
            : [$"{dto.Slug}.localhost"];

        var realm = new Realm
        {
            Id = Guid.NewGuid(),
            Slug = dto.Slug,
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            Domains = domains,
            CanManageTenants = dto.CanManageTenants,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(realm);
        await session.SaveChangesAsync(ct);

        // Per-realm OAuth seeding — standard OIDC scopes land in the new tenant DB.
        // Idempotent. Default Admin role / per-realm Setup flow is still TODO.
        await OAuthRealmSeeder.SeedAsync(_serviceProvider, dto.Slug, _logger, ct);

        // Per-realm login-provider seeding — the built-in Internal provider.
        // Lives behind an interface (impl in the Authentication slice) so we
        // can call it without taking a project ref on Authentication.
        using (var seederScope = _serviceProvider.CreateScope())
        {
            await seederScope.ServiceProvider
                .GetRequiredService<ILoginProviderRealmSeeder>()
                .SeedAsync(dto.Slug, _logger, ct);
        }

        // Per-realm App seeding — the system app `cocoar-auth` is registered
        // in every realm so app-scoped permissions resolve from day one.
        // Idempotent.
        await AppRealmSeeder.SeedAsync(_serviceProvider, dto.Slug, _logger, ct);

        _realmCache.Invalidate();
        return realm;
    }

    public async Task<ErrorOr<Realm>> UpdateRealmAsync(string slug, UpdateRealmDto dto, CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");

        if (realm.CanManageTenants && dto.IsActive == false)
        {
            var otherManagers = await session.Query<Realm>()
                .CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
            if (otherManagers == 0)
            {
                return Error.Validation("Realm.CannotDeactivateLastManager",
                    "Cannot deactivate the last realm that can manage tenants.");
            }
        }

        if (realm.CanManageTenants && dto.CanManageTenants == false)
        {
            var otherManagers = await session.Query<Realm>()
                .CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
            if (otherManagers == 0)
            {
                return Error.Validation("Realm.CannotRemoveLastManager",
                    "Cannot remove tenant management capability from the last managing realm.");
            }
        }

        if (dto.DisplayName is not null) realm.DisplayName = dto.DisplayName;
        if (dto.Description is not null) realm.Description = dto.Description;
        if (dto.Domains is not null) realm.Domains = dto.Domains;
        if (dto.CanManageTenants.HasValue) realm.CanManageTenants = dto.CanManageTenants.Value;
        if (dto.IsActive.HasValue) realm.IsActive = dto.IsActive.Value;
        realm.UpdatedAt = DateTimeOffset.UtcNow;

        session.Store(realm);
        await session.SaveChangesAsync(ct);

        _realmCache.Invalidate();
        return realm;
    }

    public async Task<ErrorOr<bool>> DeleteRealmAsync(string slug, CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");

        if (realm.CanManageTenants)
        {
            var otherManagers = await session.Query<Realm>()
                .CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
            if (otherManagers == 0)
            {
                return Error.Validation("Realm.CannotDeleteLastManager",
                    "Cannot delete the last realm that can manage tenants.");
            }
        }

        // Soft-delete: deactivate
        realm.IsActive = false;
        realm.UpdatedAt = DateTimeOffset.UtcNow;

        session.Store(realm);
        await session.SaveChangesAsync(ct);

        _realmCache.Invalidate();
        return true;
    }

    public async Task EnsureSystemRealmExistsAsync(CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == TenantConstants.SystemTenantId, ct);

        if (existing is not null)
            return;

        var systemRealm = new Realm
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = TenantConstants.SystemTenantId,
            DisplayName = "System",
            Description = "System realm for global administration",
            // Include localhost variants so dev boots work without hosts-file entries.
            Domains = ["system.localhost", "localhost", "127.0.0.1"],
            CanManageTenants = true,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(systemRealm);
        await session.SaveChangesAsync(ct);
    }
}
