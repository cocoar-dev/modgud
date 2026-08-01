using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Authorization;
using Modgud.Infrastructure.OAuth;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Scheduling;
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
    Task<ErrorOr<Realm>> CreateInitialRealmAsync(CreateRealmDto dto, CancellationToken ct = default);
    Task<ErrorOr<Realm>> ActivateInitialRealmAsync(string slug, CancellationToken ct = default);
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

    /// <summary>
    /// HARD-removes a realm: drops the tenant database entirely (event streams,
    /// signing keys, the OpenIddict token store — all gone) and deletes the global
    /// <see cref="Realm"/> record. Unlike <see cref="DeleteRealmAsync"/> (a
    /// reversible soft-delete) this is irreversible. Blocked for the control-plane
    /// realm. Sequence: deregister the tenant from Marten's registry table, then
    /// <c>DROP DATABASE ... WITH (FORCE)</c> to terminate any remaining daemon/pool
    /// backends, then remove the global record + invalidate the realm cache.
    /// </summary>
    Task<ErrorOr<bool>> HardDeleteRealmAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Compensation for a realm whose <see cref="CreateRealmAsync"/> succeeded
    /// but whose post-create bootstrap (issuing the initial-admin invite) then
    /// failed. Hard-removes the global <see cref="Realm"/> record so the
    /// partially-provisioned realm is no longer orphaned (no adminless realm
    /// left behind, no 409 on retry) and a re-run of <see cref="CreateRealmAsync"/>
    /// is clean: the tenant database and its Marten tenant-registry record are
    /// left in place and reused idempotently (CREATE DATABASE is skipped when it
    /// already exists, schema-apply and the catalog seeders are idempotent).
    ///
    /// <para>Unlike <see cref="DeleteRealmAsync"/> (a soft-delete that is
    /// deliberately blocked for the control plane) this is a hard delete. It
    /// no-ops defensively on a control-plane realm — provisioning never creates
    /// one, so reaching that branch would indicate a logic error.</para>
    /// </summary>
    Task RollbackProvisionedRealmAsync(string slug, CancellationToken ct = default);

    Task EnsureSystemRealmExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the realm that currently holds the stored
    /// <see cref="Realm.IsControlPlane"/> flag, or <c>null</c> if none does.
    /// </summary>
    Task<Realm?> GetControlPlaneRealmAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves the control-plane role to <paramref name="targetSlug"/> in one
    /// transaction: clears the flag on every other holder (self-healing any
    /// accidental &gt;1-holder state) and sets it on the target. The target
    /// must exist and be active. Idempotent when the target is already the
    /// sole control plane. After a successful move the control-plane app
    /// catalog is (idempotently) seeded into the target realm's DB and the
    /// realm cache is invalidated so the routing-gate re-reads the new flag.
    /// </summary>
    Task<ErrorOr<Realm>> TransferControlPlaneAsync(string targetSlug, CancellationToken ct = default);

    /// <summary>
    /// Registers an ALREADY-EXISTING tenant database (<c>{master}_{slug}</c>)
    /// as a realm without issuing <c>CREATE DATABASE</c> — the migration
    /// counterpart to <see cref="CreateRealmAsync"/>. Errors if the database
    /// is missing or a realm with the slug already exists. Schema is applied
    /// idempotently (existing data is kept) and the app/OAuth catalogs are
    /// seeded if missing.
    /// </summary>
    Task<ErrorOr<Realm>> AdoptExistingDatabaseAsync(
        string slug, string displayName, string[]? domains, CancellationToken ct = default);
}

public sealed class RealmProvisioningService : IRealmProvisioningService
{
    private readonly IGlobalStore _globalStore;
    private readonly IDocumentStore _tenantedStore;
    private readonly IMasterConnectionString _masterCs;
    private readonly IRealmCache _realmCache;
    private readonly IRealmMessageStorageProvisioner _messageStorageProvisioner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISecurityAuditLog _securityAudit;
    private readonly IReadOnlyList<IRealmJobScheduleObserver> _jobScheduleObservers;
    private readonly ILogger<RealmProvisioningService> _logger;

    public RealmProvisioningService(
        IGlobalStore globalStore,
        IDocumentStore tenantedStore,
        IMasterConnectionString masterCs,
        IRealmCache realmCache,
        IRealmMessageStorageProvisioner messageStorageProvisioner,
        IServiceProvider serviceProvider,
        ISecurityAuditLog securityAudit,
        IEnumerable<IRealmJobScheduleObserver> jobScheduleObservers,
        ILogger<RealmProvisioningService> logger)
    {
        _globalStore = globalStore;
        _tenantedStore = tenantedStore;
        _masterCs = masterCs;
        _realmCache = realmCache;
        _messageStorageProvisioner = messageStorageProvisioner;
        _serviceProvider = serviceProvider;
        _securityAudit = securityAudit;
        _jobScheduleObservers = jobScheduleObservers.ToList();
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

    public Task<ErrorOr<Realm>> CreateRealmAsync(CreateRealmDto dto, CancellationToken ct = default) =>
        CreateRealmCoreAsync(dto, isInitialControlPlane: false, activateImmediately: true, ct);

    /// <summary>
    /// Provisions the deployment's first realm. It is created inactive and
    /// carries the initial Control-Plane flag; the installation coordinator
    /// activates it only after the first realm administrator exists.
    /// </summary>
    public Task<ErrorOr<Realm>> CreateInitialRealmAsync(
        CreateRealmDto dto,
        CancellationToken ct = default) =>
        CreateRealmCoreAsync(dto, isInitialControlPlane: true, activateImmediately: false, ct);

    private async Task<ErrorOr<Realm>> CreateRealmCoreAsync(
        CreateRealmDto dto,
        bool isInitialControlPlane,
        bool activateImmediately,
        CancellationToken ct)
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

        // Domains are mandatory: a realm with no domain can neither route
        // requests nor build outbound links / WebAuthn RP IDs. There is no
        // silent fallback domain anymore — the caller must name at least one.
        if (dto.Domains is not { Length: > 0 })
        {
            return Error.Validation("Realm.DomainRequired",
                "At least one domain is required.");
        }

        // The primary domain (canonical public host) must be one of Domains.
        // When the caller doesn't name one, default to the first domain.
        var primaryDomain = !string.IsNullOrWhiteSpace(dto.PrimaryDomain)
            ? dto.PrimaryDomain
            : dto.Domains[0];
        if (!dto.Domains.Any(d => string.Equals(d, primaryDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Validation("Realm.PrimaryDomainNotInDomains",
                $"PrimaryDomain '{primaryDomain}' must be one of the realm's domains.");
        }

        await using var session = _globalStore.LightweightSession();
        if (isInitialControlPlane && await session.Query<Realm>().AnyAsync(ct))
        {
            return Error.Conflict(
                "Installation.RealmAlreadyExists",
                "The initial realm can only be provisioned while the deployment has no realms.");
        }

        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == dto.Slug, ct);
        if (existing is not null)
        {
            return Error.Conflict("Realm.DuplicateSlug",
                $"A realm with slug '{dto.Slug}' already exists.");
        }

        // Domain uniqueness (audit M10) — fail fast before creating a database.
        var domainClash = await CheckDomainUniquenessAsync(session, dto.Domains, selfId: null, ct);
        if (domainClash is not null) return domainClash.Value;

        // Ordinary realms never become Control Plane at creation. The sole
        // exception is the installation-only path while the registry is empty.

        // Build the tenant database connection string
        var csBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value);
        var mainDbName = csBuilder.Database!;
        var tenantDbName = $"{mainDbName}_{dto.Slug}";
        csBuilder.Database = tenantDbName;
        var tenantCs = csBuilder.ConnectionString;

        await _securityAudit.RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmProvisioned,
            TargetRealmSlug = dto.Slug,
            OutcomeCode = AuditOutcomes.Initiated,
            OperationCode = "provision-realm",
        }, ct);

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

        // Apply Marten schema to the new tenant database (tables, functions, indexes).
        // Resilient against the Solo async daemon racing this eager apply — see
        // ApplyTenantSchemaResilientlyAsync.
        var newTenantDb = await tenancy.FindOrCreateDatabase(dto.Slug);
        await ApplyTenantSchemaResilientlyAsync(
            () => newTenantDb.ApplyAllConfiguredChangesToDatabaseAsync(), dto.Slug, ct);

        // Marten tenants can be registered after Wolverine's startup resource
        // scan. Provision the transactional inbox/outbox before any handler or
        // event-forwarding path can use this realm.
        await _messageStorageProvisioner.EnsureProvisionedAsync(dto.Slug);

        var realm = new Realm
        {
            Id = Guid.NewGuid(),
            Slug = dto.Slug,
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            Domains = dto.Domains,
            PrimaryDomain = primaryDomain,
            IsControlPlane = isInitialControlPlane,
            IsActive = activateImmediately && (dto.IsActive ?? true),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(realm);
        _securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmProvisioned,
            TargetRealmSlug = dto.Slug,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "provision-realm",
        });
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
        if (realm.IsActive)
            await ReconcileJobSchedulesAsync(ct);
        return realm;
    }

    public async Task<ErrorOr<Realm>> ActivateInitialRealmAsync(
        string slug,
        CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();
        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");
        if (!realm.IsControlPlane)
            return Error.Validation(
                "Installation.InitialRealmNotControlPlane",
                "The initial realm must carry the Control-Plane flag before activation.");

        var otherActive = await session.Query<Realm>()
            .AnyAsync(r => r.Slug != slug && r.IsActive, ct);
        if (otherActive)
            return Error.Conflict(
                "Installation.OtherRealmActive",
                "Cannot activate an initial realm after another realm became active.");

        if (!realm.IsActive)
        {
            realm.IsActive = true;
            realm.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(realm);
            await session.SaveChangesAsync(ct);
        }

        _realmCache.Invalidate();
        await ReconcileJobSchedulesAsync(ct);
        return realm;
    }

    // Resilient tenant-schema apply lives in TenantSchemaProvisioning (public +
    // unit-tested). This thin wrapper binds the retry policy + warning log; the
    // call sites pass the actual apply delegate.
    private Task ApplyTenantSchemaResilientlyAsync(Func<Task> applySchema, string slug, CancellationToken ct) =>
        TenantSchemaProvisioning.ApplyWithRetryAsync(
            applySchema,
            maxAttempts: 5,
            backoff: attempt => TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1))),
            onRetry: (conflict, attempt) => _logger.LogWarning(
                "Concurrent schema-apply conflict ({SqlState}) provisioning realm {Slug} " +
                "(attempt {Attempt}); the async daemon likely raced the apply — retrying",
                conflict.SqlState, slug, attempt),
            ct);

    /// <summary>
    /// Audit M10: a domain may map to at most one ACTIVE realm. Slug uniqueness
    /// is enforced everywhere, but domain uniqueness — which is what actually
    /// drives host→tenant routing — was not. Two active realms sharing a domain
    /// make <c>RealmCache</c>'s host map nondeterministic (last row loaded wins),
    /// silently routing login/tokens/admin for that host to the wrong tenant.
    /// Compared case-insensitively; only active realms are considered because
    /// only they are routed. <paramref name="selfId"/> excludes the realm being
    /// updated from clashing with itself.
    /// </summary>
    private static async Task<Error?> CheckDomainUniquenessAsync(
        IQuerySession session, IReadOnlyCollection<string> domains, Guid? selfId, CancellationToken ct)
    {
        var wanted = domains.Select(d => d.ToLowerInvariant()).ToHashSet();
        var activeRealms = await session.Query<Realm>().Where(r => r.IsActive).ToListAsync(ct);

        foreach (var other in activeRealms)
        {
            if (selfId is { } id && other.Id == id) continue;
            var clash = other.Domains.FirstOrDefault(d => wanted.Contains(d.ToLowerInvariant()));
            if (clash is not null)
                return Error.Conflict("Realm.DomainTaken",
                    $"The domain '{clash}' is already claimed by the active realm '{other.Slug}'. Each domain must map to exactly one active realm.");
        }
        return null;
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

        // When a new domain set is supplied it must be non-empty — a realm
        // can never end up with zero domains.
        if (dto.Domains is not null && dto.Domains.Length == 0)
        {
            return Error.Validation("Realm.DomainRequired",
                "At least one domain is required.");
        }

        if (dto.DisplayName is not null) realm.DisplayName = dto.DisplayName;
        if (dto.Description is not null) realm.Description = dto.Description;
        if (dto.Domains is not null) realm.Domains = dto.Domains;

        // The domain set after applying the patch — the basis for the
        // primary-domain invariant below.
        var resultingDomains = realm.Domains;

        if (dto.PrimaryDomain is not null)
        {
            // Explicit primary change: it must be one of the resulting domains.
            if (!resultingDomains.Any(d => string.Equals(d, dto.PrimaryDomain, StringComparison.OrdinalIgnoreCase)))
            {
                return Error.Validation("Realm.PrimaryDomainNotInDomains",
                    $"PrimaryDomain '{dto.PrimaryDomain}' must be one of the realm's domains.");
            }
            realm.PrimaryDomain = dto.PrimaryDomain;
        }
        else if (!resultingDomains.Any(d => string.Equals(d, realm.PrimaryDomain, StringComparison.OrdinalIgnoreCase)))
        {
            // The domain set changed and dropped the old primary without a
            // replacement given — refuse rather than silently re-pointing the
            // canonical host (and silently invalidating passkeys).
            return Error.Validation("Realm.PrimaryDomainDropped",
                $"The new domain set no longer contains the current PrimaryDomain '{realm.PrimaryDomain}'. Provide a new PrimaryDomain that is in the domain set.");
        }

        if (dto.IsActive.HasValue) realm.IsActive = dto.IsActive.Value;
        realm.UpdatedAt = DateTimeOffset.UtcNow;

        // Domain uniqueness (audit M10) — only an active realm contends for a
        // host. A reactivation or a domain change must not collide with another
        // active realm's domain.
        if (realm.IsActive)
        {
            var domainClash = await CheckDomainUniquenessAsync(session, realm.Domains, selfId: realm.Id, ct);
            if (domainClash is not null) return domainClash.Value;
        }

        session.Store(realm);
        await session.SaveChangesAsync(ct);

        _realmCache.Invalidate();
        await ReconcileJobSchedulesAsync(ct);
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
        await ReconcileJobSchedulesAsync(ct);
        return true;
    }

    public async Task<ErrorOr<bool>> HardDeleteRealmAsync(string slug, CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");

        // Same guard as DeleteRealmAsync: the Control-Plane realm holds the
        // global administration surface and must never be dropped.
        if (realm.IsControlPlane)
        {
            return Error.Validation("Realm.CannotDeleteControlPlane",
                "Cannot hard-delete the Control-Plane realm — the deployment would lose its global administration surface.");
        }

        var csBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value);
        var mainDbName = csBuilder.Database!;
        var tenantDbName = $"{mainDbName}_{slug}";

        await _securityAudit.RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmProvisioned,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Initiated,
            OperationCode = "hard-delete",
            ReasonCode = "operator-request",
        }, ct);

        // 1. Hand the tenant back to Marten. RemoveTenantAsync evicts it from the
        //    tenancy's in-memory cache, disposes its Npgsql data source (gracefully
        //    closing the pool before the drop) and deletes the registry row in
        //    realms.mt_tenant_databases, so the async daemon stops rediscovering the
        //    database and it drops out of tenant resolution.
        //
        //    Caveat — re-creating a realm with the SAME slug in the SAME process:
        //    Weasel's DefaultNpgsqlDataSourceFactory caches data sources by connection
        //    string with no per-key eviction, so the disposed data source would be
        //    handed back on a later create with the identical connection string. Realm
        //    slugs are unique per lifecycle (tests use unique slugs too), so this does
        //    not arise on the normal path; a custom evictable INpgsqlDataSourceFactory
        //    is the clean fix if in-process slug reuse is ever required.
        var tenancy = (Marten.Storage.MasterTableTenancy)_tenantedStore.Options.Tenancy;
        await tenancy.RemoveTenantAsync(slug);

        // 2. DROP DATABASE ... WITH (FORCE) on the maintenance DB. Marten holds one
        //    Npgsql data source (its own pool plus the async daemon's connection) per
        //    tenant DB; FORCE (PG13+) terminates every remaining backend so the drop
        //    succeeds without a "database is being accessed by other users" error.
        var bootstrapBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value) { Database = "postgres" };
        await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
        {
            await bootstrapConn.OpenAsync(ct);
            var quotedName = "\"" + tenantDbName.Replace("\"", "\"\"") + "\"";
#pragma warning disable CA2100 // tenantDbName derives from the operator connection string + a validated slug, never raw request input
            await using var dropCmd = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {quotedName} WITH (FORCE)", bootstrapConn);
#pragma warning restore CA2100
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Remove the global Realm record and invalidate the cache so middleware
        //    stops resolving the now-dropped realm.
        session.Delete(realm);
        _securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmProvisioned,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "hard-delete",
            ReasonCode = "operator-request",
        });
        await session.SaveChangesAsync(ct);
        _realmCache.Invalidate();
        await ReconcileJobSchedulesAsync(ct);

        _logger.LogWarning(
            "Hard-deleted realm {Slug}: dropped tenant database {DbName} and removed the global Realm record. " +
            "Irreversible — event streams, signing keys and the OpenIddict token store are gone.",
            slug, tenantDbName);

        return true;
    }

    public async Task RollbackProvisionedRealmAsync(string slug, CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var realm = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (realm is null)
            return;

        if (realm.IsControlPlane && realm.IsActive)
        {
            // An active Control Plane is the deployment's administration
            // anchor. The installation path deliberately creates its first
            // realm inactive, so that partial realm may be compensated safely.
            _logger.LogError(
                "Refusing to roll back realm {Slug}: it holds the control-plane flag. " +
                "Only an inactive, partially installed initial realm may be rolled back.",
                slug);
            return;
        }

        session.Delete(realm);
        _securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmProvisioned,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "rollback-provisioning",
            ReasonCode = "bootstrap-invite-failed",
        });
        await session.SaveChangesAsync(ct);

        _realmCache.Invalidate();
        await ReconcileJobSchedulesAsync(ct);

        _logger.LogWarning(
            "Rolled back partially-provisioned realm {Slug} after a post-create bootstrap failure. " +
            "The tenant database is left in place for idempotent reuse on retry.",
            slug);
    }

    public async Task EnsureSystemRealmExistsAsync(CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == TenantConstants.SystemTenantId, ct);

        if (existing is not null)
        {
            var dirty = false;

            // Adopt the control-plane flag onto the bootstrap realm ONLY when
            // no realm currently holds it. This guard is load-bearing: it makes
            // a TransferControlPlaneAsync durable across reboots — without it
            // every boot would steal the flag back to the system realm and
            // silently undo the transfer. (On a fresh upgrade from the old
            // computed-flag model the system doc already deserializes with
            // IsControlPlane=true, so this is a no-op there too.)
            if (!existing.IsControlPlane
                && !await session.Query<Realm>().AnyAsync(r => r.IsControlPlane, ct))
            {
                existing.IsControlPlane = true;
                dirty = true;
            }

            if (dirty)
            {
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                session.Store(existing);
                await session.SaveChangesAsync(ct);
            }
        }
        else
        {
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
                // Canonical public host for the system realm. "localhost" is a
                // browser secure-context, so passkeys + outbound links work in dev
                // without a hosts-file entry. Production deploys add their public
                // hostname (recover realm-add-domain) and point the primary at it
                // (recover realm-set-primary-domain).
                PrimaryDomain = "localhost",
                // The bootstrap realm is the control plane at first boot. The flag
                // is transferable thereafter (see TransferControlPlaneAsync).
                IsControlPlane = true,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            session.Store(systemRealm);
            await session.SaveChangesAsync(ct);
        }

        // Upgrade backfill for EVERY realm (not just system): a realm doc
        // persisted before the PrimaryDomain field existed deserializes with an
        // empty value, which would break its outbound links + WebAuthn RP. Set
        // it to the first domain so the "PrimaryDomain ∈ Domains, non-empty"
        // invariant holds deployment-wide — not only for the control-plane
        // realm. Loaded + filtered in memory (realm count is tiny) to be robust
        // against a missing JSON key vs an empty string. Idempotent — a no-op
        // once every realm has a primary.
        var allRealms = await session.Query<Realm>().ToListAsync(ct);
        var backfilled = false;
        foreach (var r in allRealms)
        {
            if (string.IsNullOrWhiteSpace(r.PrimaryDomain) && r.Domains is { Length: > 0 })
            {
                r.PrimaryDomain = r.Domains[0];
                r.UpdatedAt = DateTimeOffset.UtcNow;
                session.Store(r);
                backfilled = true;
            }
        }
        if (backfilled)
            await session.SaveChangesAsync(ct);
    }

    public async Task<Realm?> GetControlPlaneRealmAsync(CancellationToken ct = default)
    {
        await using var session = _globalStore.QuerySession();
        return await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.IsControlPlane, ct);
    }

    public async Task<ErrorOr<Realm>> TransferControlPlaneAsync(
        string targetSlug, CancellationToken ct = default)
    {
        await using var session = _globalStore.LightweightSession();

        var target = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == targetSlug, ct);
        if (target is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{targetSlug}' not found.");

        if (!target.IsActive)
            return Error.Validation("Realm.TargetInactive",
                "Cannot transfer the control plane to an inactive realm.");

        // Load EVERY other holder (not just "the one") so an accidental
        // multi-holder state self-heals down to exactly the target.
        var otherHolders = await session.Query<Realm>()
            .Where(r => r.IsControlPlane && r.Slug != targetSlug)
            .ToListAsync(ct);

        // True no-op: target is already the sole control plane.
        if (otherHolders.Count == 0 && target.IsControlPlane)
            return target;

        foreach (var holder in otherHolders)
        {
            holder.IsControlPlane = false;
            holder.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(holder);
        }

        target.IsControlPlane = true;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(target);

        _securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.ControlPlaneTransferred,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = targetSlug,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "transfer",
            Count = otherHolders.Count,
        });
        await session.SaveChangesAsync(ct);

        // The flag move is committed. Invalidate the cache NOW (load-bearing —
        // the routing gate must follow the new flag) BEFORE the best-effort
        // re-seed below, so a seed failure can't leave the gate stale.
        _realmCache.Invalidate();

        // Make the target a fully-equal control plane: seed the control-plane
        // app catalog into its tenant DB so scoped control-plane:realm:* roles
        // can be granted there. The realm's existing realm:admin already passes
        // the gate via the realm-wide bypass tier, so this is for delegation
        // completeness, not lockout avoidance — hence best-effort: a failure
        // here must NOT surface as a 500/throw from this already-committed
        // mutation (it self-heals on the next boot or transfer). Idempotent.
        // (The demoted realm keeps its now-inert control-plane app — the gate
        // 404s its host anyway; cleaning it up is optional, not load-bearing.)
        try
        {
            await AppRealmSeeder.SeedAsync(
                _serviceProvider, targetSlug, isControlPlane: true, _logger, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Control plane moved to realm {Slug}, but seeding the control-plane app into its DB failed — it self-heals on the next boot or transfer.",
                targetSlug);
        }

        await ReconcileJobSchedulesAsync(ct);

        _logger.LogWarning(
            "Control plane transferred to realm {Slug} (cleared {Count} previous holder(s))",
            targetSlug, otherHolders.Count);
        return target;
    }

    public async Task<ErrorOr<Realm>> AdoptExistingDatabaseAsync(
        string slug, string displayName, string[]? domains, CancellationToken ct = default)
    {
        if (!RealmSlugRules.IsValidFormat(slug))
        {
            return Error.Validation("Realm.InvalidSlug",
                "Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens.");
        }

        if (RealmSlugRules.IsReserved(slug))
        {
            return Error.Validation("Realm.ReservedSlug",
                $"The slug '{slug}' is reserved and cannot be used.");
        }

        // Domains are mandatory — same reasoning as CreateRealmAsync. No
        // silent `.localhost` fallback: the operator names the host(s) that
        // route to the adopted database.
        if (domains is not { Length: > 0 })
        {
            return Error.Validation("Realm.DomainRequired",
                "At least one domain is required.");
        }

        await using var session = _globalStore.LightweightSession();
        var existing = await session.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (existing is not null)
        {
            return Error.Conflict("Realm.DuplicateSlug",
                $"A realm with slug '{slug}' already exists.");
        }

        // Domain uniqueness (audit M10) — same invariant as create.
        var domainClash = await CheckDomainUniquenessAsync(session, domains, selfId: null, ct);
        if (domainClash is not null) return domainClash.Value;

        var csBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value);
        var mainDbName = csBuilder.Database!;
        var tenantDbName = $"{mainDbName}_{slug}";
        csBuilder.Database = tenantDbName;
        var tenantCs = csBuilder.ConnectionString;

        // adopt does NOT create the database — it registers an existing one.
        var bootstrapBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value) { Database = "postgres" };
        await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
        {
            await bootstrapConn.OpenAsync(ct);
            await using var checkDbCmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @dbName", bootstrapConn);
            checkDbCmd.Parameters.AddWithValue("@dbName", tenantDbName);
            if (await checkDbCmd.ExecuteScalarAsync(ct) is null)
            {
                return Error.NotFound("Realm.DatabaseMissing",
                    $"Database '{tenantDbName}' does not exist. adopt-tenant registers an EXISTING database — create and restore it first.");
            }
        }

        await _securityAudit.RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmAdopted,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Initiated,
            OperationCode = "adopt-database",
        }, ct);

        // Register in Marten's tenant registry + apply schema idempotently
        // (existing data is preserved; this only adds missing tables/indexes).
        var tenancy = (Marten.Storage.MasterTableTenancy)_tenantedStore.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync(slug, tenantCs);
        var adoptedDb = await tenancy.FindOrCreateDatabase(slug);
        await ApplyTenantSchemaResilientlyAsync(
            () => adoptedDb.ApplyAllConfiguredChangesToDatabaseAsync(), slug, ct);
        await _messageStorageProvisioner.EnsureProvisionedAsync(slug);

        var realm = new Realm
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = displayName,
            Domains = domains,
            PrimaryDomain = domains[0],
            IsControlPlane = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(realm);
        _securityAudit.StorePlatformRequired(session, new PlatformAuditRecord
        {
            EventType = AuditEvents.RealmAdopted,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Completed,
            OperationCode = "adopt-database",
        });
        await session.SaveChangesAsync(ct);

        // Idempotent catalog seeding — won't clobber existing rows in the
        // adopted DB; only fills in what the schema-apply doesn't cover.
        await OAuthRealmSeeder.SeedAsync(_serviceProvider, slug, _logger, ct);
        using (var seederScope = _serviceProvider.CreateScope())
        {
            await seederScope.ServiceProvider
                .GetRequiredService<ILoginProviderRealmSeeder>()
                .SeedAsync(slug, _logger, ct);
        }
        await AppRealmSeeder.SeedAsync(_serviceProvider, slug, isControlPlane: false, _logger, ct);

        _realmCache.Invalidate();
        await ReconcileJobSchedulesAsync(ct);
        _logger.LogInformation("Adopted existing database {DbName} as realm {Slug}", tenantDbName, slug);
        return realm;
    }

    private async Task ReconcileJobSchedulesAsync(CancellationToken ct)
    {
        foreach (var observer in _jobScheduleObservers)
        {
            try
            {
                await observer.ReconcileAsync(ct);
            }
            catch (Exception ex)
            {
                // Realm mutations are already committed at every call site.
                // Scheduling is in-memory and self-heals on restart, so a
                // reconcile failure must not turn a successful lifecycle
                // operation into a misleading HTTP/CLI failure.
                _logger.LogError(ex,
                    "Realm lifecycle mutation committed, but Quartz schedules could not be reconciled");
            }
        }
    }
}
