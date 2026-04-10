using System.Text.RegularExpressions;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Interfaces;
using Cocoar.Auth.Infrastructure.OpenIddict;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Repositories;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for managing realm lifecycle: creation, updates, soft-delete, and DB provisioning.
/// Uses Marten for realm CRUD (stored as documents in the system tenant).
/// Raw SQL only for PostgreSQL database creation and Marten tenant registry.
/// </summary>
public interface IRealmProvisioningService
{
	Task<List<Realm>> GetAllRealmsAsync(CancellationToken ct = default);
	Task<Realm?> GetRealmBySlugAsync(string slug, CancellationToken ct = default);
	Task<ErrorOr<Realm>> CreateRealmAsync(CreateRealmDto dto, CancellationToken ct = default);
	Task<ErrorOr<Realm>> UpdateRealmAsync(string slug, UpdateRealmDto dto, CancellationToken ct = default);
	Task<ErrorOr<bool>> DeleteRealmAsync(string slug, CancellationToken ct = default);
	Task<bool> NeedsSetupAsync(string slug, CancellationToken ct = default);
	Task EnsureSystemRealmExistsAsync(CancellationToken ct = default);
}

public class RealmProvisioningService : IRealmProvisioningService
{
	private readonly IDocumentStore _store;
	private readonly IMasterConnectionString _masterCs;
	private readonly IRealmCache _realmCache;
	private readonly IServiceProvider _serviceProvider;
	private readonly ILogger<RealmProvisioningService> _logger;

	private const string SystemTenantId = "system";

	private static readonly Regex SlugRegex = new(@"^[a-z][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.Compiled);

	private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
	{
		"system", "health", "swagger"
	};

	public RealmProvisioningService(
		IDocumentStore store,
		IMasterConnectionString masterCs,
		IRealmCache realmCache,
		IServiceProvider serviceProvider,
		ILogger<RealmProvisioningService> logger)
	{
		_store = store;
		_masterCs = masterCs;
		_realmCache = realmCache;
		_serviceProvider = serviceProvider;
		_logger = logger;
	}

	public async Task<List<Realm>> GetAllRealmsAsync(CancellationToken ct = default)
	{
		await using var session = _store.QuerySession(SystemTenantId);
		var realms = await session.Query<Realm>()
			.OrderBy(r => r.CreatedAt)
			.ToListAsync(ct);
		return realms.ToList();
	}

	public async Task<Realm?> GetRealmBySlugAsync(string slug, CancellationToken ct = default)
	{
		await using var session = _store.QuerySession(SystemTenantId);
		return await session.Query<Realm>()
			.FirstOrDefaultAsync(r => r.Slug == slug, ct);
	}

	public async Task<ErrorOr<Realm>> CreateRealmAsync(CreateRealmDto dto, CancellationToken ct = default)
	{
		// Validate slug format
		if (!SlugRegex.IsMatch(dto.Slug))
		{
			return Error.Validation("Realm.InvalidSlug",
				"Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens.");
		}

		// Check reserved slugs
		if (ReservedSlugs.Contains(dto.Slug))
		{
			return Error.Validation("Realm.ReservedSlug",
				$"The slug '{dto.Slug}' is reserved and cannot be used.");
		}

		// Check uniqueness via Marten
		await using var session = _store.LightweightSession(SystemTenantId);
		var existing = await session.Query<Realm>()
			.FirstOrDefaultAsync(r => r.Slug == dto.Slug, ct);

		if (existing is not null)
		{
			return Error.Conflict("Realm.DuplicateSlug",
				$"A realm with slug '{dto.Slug}' already exists.");
		}

		// Build the tenant database connection string
		var csBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value);
		var masterDbName = csBuilder.Database!;
		var baseDbName = masterDbName.EndsWith("_master")
			? masterDbName[..^"_master".Length]
			: masterDbName;
		var tenantDbName = $"{baseDbName}_{dto.Slug}";
		csBuilder.Database = tenantDbName;
		var tenantCs = csBuilder.ConnectionString;

		// Raw SQL: Create the PostgreSQL database (DDL — cannot use Marten)
		var bootstrapBuilder = new NpgsqlConnectionStringBuilder(_masterCs.Value) { Database = "postgres" };
		await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
		{
			await bootstrapConn.OpenAsync(ct);
			await using var checkDbCmd = new NpgsqlCommand(
				$"SELECT 1 FROM pg_database WHERE datname = '{tenantDbName}'", bootstrapConn);
			if (await checkDbCmd.ExecuteScalarAsync(ct) is null)
			{
				await using var createDbCmd = new NpgsqlCommand(
					$"CREATE DATABASE \"{tenantDbName}\"", bootstrapConn);
				await createDbCmd.ExecuteNonQueryAsync(ct);
				_logger.LogInformation("Created database {DbName} for realm {Slug}", tenantDbName, dto.Slug);
			}
		}

		// Register in Marten's tenant registry via its built-in API
		var tenancy = (Marten.Storage.MasterTableTenancy)_store.Options.Tenancy;
		await tenancy.AddDatabaseRecordAsync(dto.Slug, tenantCs);

		// Apply Marten schema to the new tenant database (tables, functions, indexes)
		var newTenantDb = await tenancy.FindOrCreateDatabase(dto.Slug);
		await newTenantDb.ApplyAllConfiguredChangesToDatabaseAsync();

		// Auto-generate domains if not provided
		var domains = dto.Domains is { Length: > 0 }
			? dto.Domains
			: [$"{dto.Slug}.localhost"];

		// Store realm metadata as Marten document in system tenant
		var realm = new Realm
		{
			Id = Guid.NewGuid(),
			Slug = dto.Slug,
			DisplayName = dto.DisplayName,
			Description = dto.Description,
			Domains = domains,
			CanManageTenants = dto.CanManageTenants,
			IsActive = true,
			CreatedAt = DateTimeOffset.UtcNow
		};

		session.Store(realm);
		await session.SaveChangesAsync(ct);

		// Seed default data into the new tenant
		try
		{
			await _serviceProvider.SeedOpenIddictScopesAsync(dto.Slug);
			await _serviceProvider.SeedLoginProvidersAsync(dto.Slug);
			_logger.LogInformation("Seeded default data for realm {Slug}", dto.Slug);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to seed default data for realm {Slug}", dto.Slug);
		}

		_realmCache.Invalidate();

		return realm;
	}

	public async Task<ErrorOr<Realm>> UpdateRealmAsync(string slug, UpdateRealmDto dto, CancellationToken ct = default)
	{
		await using var session = _store.LightweightSession(SystemTenantId);

		var realm = await session.Query<Realm>()
			.FirstOrDefaultAsync(r => r.Slug == slug, ct);

		if (realm is null)
		{
			return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");
		}

		// Cannot deactivate the last tenant that can manage tenants
		if (realm.CanManageTenants && dto.IsActive == false)
		{
			var otherManagers = await session.Query<Realm>()
				.CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
			if (otherManagers == 0)
			{
				return Error.Validation("Realm.CannotDeactivateLastManager",
					"Cannot deactivate the last tenant that can manage tenants.");
			}
		}

		// Cannot remove CanManageTenants from the last managing tenant
		if (realm.CanManageTenants && dto.CanManageTenants == false)
		{
			var otherManagers = await session.Query<Realm>()
				.CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
			if (otherManagers == 0)
			{
				return Error.Validation("Realm.CannotRemoveLastManager",
					"Cannot remove tenant management capability from the last managing tenant.");
			}
		}

		// Apply updates
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
		await using var session = _store.LightweightSession(SystemTenantId);

		var realm = await session.Query<Realm>()
			.FirstOrDefaultAsync(r => r.Slug == slug, ct);

		if (realm is null)
		{
			return Error.NotFound("Realm.NotFound", $"Realm '{slug}' not found.");
		}

		// Cannot delete the last tenant that can manage tenants
		if (realm.CanManageTenants)
		{
			var otherManagers = await session.Query<Realm>()
				.CountAsync(r => r.CanManageTenants && r.IsActive && r.Slug != slug, ct);
			if (otherManagers == 0)
			{
				return Error.Validation("Realm.CannotDeleteLastManager",
					"Cannot delete the last tenant that can manage tenants.");
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

	public async Task<bool> NeedsSetupAsync(string slug, CancellationToken ct = default)
	{
		// A realm needs setup if it has no users with the Admin role
		try
		{
			await using var session = _store.QuerySession(slug);

			var adminRole = await session.Query<RoleState>()
				.FirstOrDefaultAsync(r => r.NormalizedName == "ADMIN" && !r.IsDeleted, ct);

			if (adminRole is null)
				return true;

			var adminUser = await session.Query<ApplicationUser>()
				.FirstOrDefaultAsync(u => u.Roles.Contains(adminRole.Id) && u.IsActive, ct);

			return adminUser is null;
		}
		catch
		{
			return true;
		}
	}

	public async Task EnsureSystemRealmExistsAsync(CancellationToken ct = default)
	{
		await using var session = _store.LightweightSession(SystemTenantId);

		var existing = await session.Query<Realm>()
			.FirstOrDefaultAsync(r => r.Slug == SystemTenantId, ct);

		if (existing is not null)
			return;

		var systemRealm = new Realm
		{
			Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
			Slug = "system",
			DisplayName = "System",
			Description = "System realm for global administration",
			Domains = ["system.localhost"],
			CanManageTenants = true,
			IsActive = true,
			CreatedAt = DateTimeOffset.UtcNow
		};

		session.Store(systemRealm);
		await session.SaveChangesAsync(ct);
	}
}
