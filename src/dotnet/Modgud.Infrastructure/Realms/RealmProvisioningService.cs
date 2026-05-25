using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Authorization;
using Modgud.Infrastructure.OAuth;
using Modgud.Infrastructure.Persistence.Tenancy;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Modgud.Infrastructure.Realms;

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
    /// <summary>
    /// Patches a realm's structural metadata (DisplayName, Description,
    /// Domains, IsActive). Tenant-owned settings (self-registration etc.)
    /// live in the tenant-DB <c>RealmSettings</c> aggregate and have
    /// their own endpoint — see <c>RealmSettingsEndpoints</c>.
    /// </summary>
    Task<ErrorOr<Realm>> UpdateRealmAsync(
        string slug,
        UpdateRealmDto dto,
        CancellationToken ct = default);
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

        // C15c — InitialAdmin is mandatory: a realm without an admin path
        // is unusable, and the only way to onboard the first admin
        // post-creation is via Recovery-CLI (filesystem trust). Forcing
        // an Email here prevents accidentally provisioning a tenant the
        // recipient can't ever activate.
        if (string.IsNullOrWhiteSpace(dto.InitialAdmin?.UserName))
        {
            return Error.Validation("Realm.InitialAdminUserNameRequired",
                "InitialAdmin.UserName is required.");
        }
        if (string.IsNullOrWhiteSpace(dto.InitialAdmin.Email) || !dto.InitialAdmin.Email.Contains('@'))
        {
            return Error.Validation("Realm.InitialAdminEmailRequired",
                "InitialAdmin.Email is required and must be a valid address.");
        }

        await using var session = _globalStore.LightweightSession();
        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == dto.Slug, ct);
        if (existing is not null)
        {
            return Error.Conflict("Realm.DuplicateSlug",
                $"A realm with slug '{dto.Slug}' already exists.");
        }

        // No Control-Plane validation needed: IsControlPlane is computed
        // from `Slug == RealmSlugRules.SystemSlug`, the slug "system" is
        // reserved (no caller can claim it via CreateRealm), and the
        // system realm is seeded once in EnsureSystemRealmExistsAsync.
        // Therefore "exactly one Control Plane per deployment" is a
        // consequence of the slug being immutable + reserved, not a
        // separately-enforced invariant.

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
                // CA2100: PostgreSQL DDL doesn't accept parameter binding for
                // object names. dto.Slug was validated by RealmSlugRules
                // (regex ^[a-z][a-z0-9-]{1,61}[a-z0-9]$ + reserved list)
                // before this line, so tenantDbName is restricted to
                // [a-z0-9_-] and cannot contain SQL meta-characters. The
                // quoted-identifier escaping above is defense-in-depth.
#pragma warning disable CA2100
                await using var createDbCmd = new NpgsqlCommand(
                    $"CREATE DATABASE {quotedName}", bootstrapConn);
#pragma warning restore CA2100
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

        // Per-realm App seeding — the system app `modgud` is registered
        // in every realm so app-scoped permissions resolve from day one.
        // The Control-Plane app (cross-realm admin surface) is seeded ONLY
        // when this realm is itself the Control Plane — tenant realms
        // never see those permissions, even if their hostname were
        // misconfigured. Idempotent.
        await AppRealmSeeder.SeedAsync(
            _serviceProvider,
            dto.Slug,
            isControlPlane: realm.IsControlPlane,
            _logger,
            ct);

        _realmCache.Invalidate();
        return realm;
    }

    public async Task<ErrorOr<Realm>> UpdateRealmAsync(
        string slug,
        UpdateRealmDto dto,
        CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");

        // The Control-Plane realm (slug = "system") is the architectural
        // anchor: no /api/admin/realms surface and no cross-realm admin
        // exists without it. Deactivating it would lock the deployment
        // out of its own administration. Block it.
        if (realm.IsControlPlane && dto.IsActive == false)
        {
            return Error.Validation("Realm.CannotDeactivateControlPlane",
                "Cannot deactivate the Control-Plane realm — the deployment would lose its global administration surface.");
        }

        if (dto.DisplayName is not null) realm.DisplayName = dto.DisplayName;
        if (dto.Description is not null) realm.Description = dto.Description;
        if (dto.Domains is not null) realm.Domains = dto.Domains;
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

        // Same reasoning as UpdateRealmAsync's deactivate guard: the
        // Control-Plane realm is required for cross-realm administration
        // and can't be deleted without locking the deployment out.
        if (realm.IsControlPlane)
        {
            return Error.Validation("Realm.CannotDeleteControlPlane",
                "Cannot delete the Control-Plane realm — the deployment would lose its global administration surface.");
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

        if (existing is not null) return;

        var systemRealm = new Realm
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = TenantConstants.SystemTenantId,
            DisplayName = "System",
            Description = "System realm for global administration",
            // Include localhost variants so dev boots work without hosts-file entries.
            // Production deploys must add their public hostname via the Recovery CLI:
            //   recover realm-add-domain --slug system --domain auth.example.com
            Domains = ["system.localhost", "localhost", "127.0.0.1"],
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(systemRealm);
        await session.SaveChangesAsync(ct);
    }
}
