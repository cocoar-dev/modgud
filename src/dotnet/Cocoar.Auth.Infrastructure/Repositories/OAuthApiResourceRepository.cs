using System.Security.Cryptography;
using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Infrastructure.Repositories;

/// <summary>
/// Marten-based repository for OAuth API resources.
/// Uses event sourcing for domain data and document storage for security data.
/// </summary>
public class OAuthApiResourceRepository : IOAuthApiResourceRepository
{
	private readonly ITenantSessionFactory _sessionFactory;

	public OAuthApiResourceRepository(ITenantSessionFactory sessionFactory)
	{
		_sessionFactory = sessionFactory;
	}

	public async Task<OAuthApiResourceListDto> GetAllAsync(
		PaginationRequest pagination,
		CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenQuerySession();

		var query = session.Query<OAuthApiResourceState>()
			.Where(x => !x.IsDeleted);

		var totalCount = await query.CountAsync(cancellationToken);

		var resources = await query
			.Skip((pagination.Page - 1) * pagination.PageSize)
			.Take(pagination.PageSize)
			.ToListAsync(cancellationToken);

		var items = new List<OAuthApiResourceDto>();
		foreach (var resource in resources)
		{
			items.Add(await MapToDtoAsync(session, resource, cancellationToken));
		}

		return new OAuthApiResourceListDto
		{
			Items = items,
			TotalCount = totalCount
		};
	}

	public async Task<OAuthApiResourceDto?> GetByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return null;
		}

		await using var session = _sessionFactory.OpenQuerySession();
		var resource = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);

		if (resource is null || resource.IsDeleted)
		{
			return null;
		}

		return await MapToDtoAsync(session, resource, cancellationToken);
	}

	public async Task<ErrorOr<OAuthApiResourceCreatedDto>> CreateAsync(
		CreateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenSession();

		// Check if API resource name already exists
		var existing = await session.Query<OAuthApiResourceState>()
			.FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, cancellationToken);

		if (existing is not null)
		{
			return OAuthErrors.ApiResourceNameAlreadyExists(dto.Name);
		}

		var id = Guid.NewGuid();

		// Create the aggregate and emit the creation event
		var (_, createdEvent) = OAuthApiResourceAggregate.Create(
			id,
			dto.Name,
			dto.DisplayName,
			dto.Description,
			dto.Enabled,
			dto.Scopes);

		session.Events.StartStream<OAuthApiResourceAggregate>(id, createdEvent);

		// Emit user claims event if specified
		if (dto.UserClaims.Count > 0)
		{
			session.Events.Append(id, new OAuthApiResourceUserClaimsChanged(id, dto.UserClaims));
		}

		// Generate and store API secret
		var apiSecret = GenerateSecret();
		var securityData = OAuthApiResourceSecurityData.Create(id);
		securityData.ApiSecret = HashSecret(apiSecret);

		// Also add to the Secrets list with metadata
		securityData.Secrets.Add(new ApiSecretEntry
		{
			Type = "SharedSecret",
			HashedValue = securityData.ApiSecret,
			Description = "Initial secret",
			CreatedAt = DateTimeOffset.UtcNow
		});

		session.Store(securityData);

		await session.SaveChangesAsync(cancellationToken);

		return new OAuthApiResourceCreatedDto
		{
			Id = id.ToString(),
			Name = dto.Name,
			DisplayName = dto.DisplayName,
			Description = dto.Description,
			Enabled = dto.Enabled,
			Scopes = dto.Scopes,
			UserClaims = dto.UserClaims,
			ApiSecret = apiSecret
		};
	}

	public async Task<ErrorOr<OAuthApiResourceDto>> UpdateAsync(
		string id,
		UpdateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		var aggregate = await session.Events.AggregateStreamAsync<OAuthApiResourceAggregate>(guid, token: cancellationToken);
		if (aggregate is null)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		// Emit events for changed properties
		if (dto.DisplayName is not null && dto.DisplayName != currentState.DisplayName)
		{
			var evt = aggregate.SetDisplayName(dto.DisplayName);
			session.Events.Append(guid, evt);
		}

		if (dto.Description is not null && dto.Description != currentState.Description)
		{
			var evt = aggregate.SetDescription(dto.Description);
			session.Events.Append(guid, evt);
		}

		if (dto.Enabled.HasValue && dto.Enabled.Value != currentState.Enabled)
		{
			if (dto.Enabled.Value)
			{
				var evt = aggregate.Enable();
				session.Events.Append(guid, evt);
			}
			else
			{
				var evt = aggregate.Disable();
				session.Events.Append(guid, evt);
			}
		}

		if (dto.Scopes is not null && !dto.Scopes.SequenceEqual(currentState.Scopes))
		{
			var evt = aggregate.SetScopes(dto.Scopes);
			session.Events.Append(guid, evt);
		}

		if (dto.UserClaims is not null && !dto.UserClaims.SequenceEqual(currentState.UserClaims))
		{
			var evt = aggregate.SetUserClaims(dto.UserClaims);
			session.Events.Append(guid, evt);
		}

		await session.SaveChangesAsync(cancellationToken);

		// Reload the state to get updated values
		var updatedState = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);
		return await MapToDtoAsync(session, updatedState!, cancellationToken);
	}

	public async Task<ErrorOr<bool>> DeleteAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var aggregate = await session.Events.AggregateStreamAsync<OAuthApiResourceAggregate>(guid, token: cancellationToken);
		if (aggregate is null || aggregate.IsDeleted)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		var deletedEvent = aggregate.Delete();
		session.Events.Append(guid, deletedEvent);

		// Also delete the security data
		session.Delete<OAuthApiResourceSecurityData>(guid);

		await session.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<ErrorOr<ApiSecretDto>> RegenerateSecretAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		var newSecret = GenerateSecret();

		var securityData = await session.LoadAsync<OAuthApiResourceSecurityData>(guid, cancellationToken)
			?? OAuthApiResourceSecurityData.Create(guid);

		securityData.ApiSecret = HashSecret(newSecret);
		securityData.UpdateConcurrencyToken();
		session.Store(securityData);

		await session.SaveChangesAsync(cancellationToken);

		return new ApiSecretDto { ApiSecret = newSecret };
	}

	public async Task<ErrorOr<ApiSecretCreatedDto>> CreateSecretAsync(
		string id,
		CreateApiSecretDto dto,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		var securityData = await session.LoadAsync<OAuthApiResourceSecurityData>(guid, cancellationToken)
			?? OAuthApiResourceSecurityData.Create(guid);

		var newSecret = GenerateSecret();
		var hashedSecret = HashSecret(newSecret);
		var secretEntry = new ApiSecretEntry
		{
			Type = dto.Type,
			HashedValue = hashedSecret,
			Description = dto.Description,
			Expiration = dto.Expiration,
			CreatedAt = DateTimeOffset.UtcNow
		};

		securityData.Secrets.Add(secretEntry);

		// Also update the legacy ApiSecret field to the latest secret
		securityData.ApiSecret = hashedSecret;
		securityData.UpdateConcurrencyToken();
		session.Store(securityData);

		await session.SaveChangesAsync(cancellationToken);

		return new ApiSecretCreatedDto
		{
			SecretId = secretEntry.SecretId.ToString(),
			ApiSecret = newSecret
		};
	}

	public async Task<ErrorOr<bool>> DeleteSecretAsync(
		string id,
		string secretId,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		if (!Guid.TryParse(secretId, out var secretGuid))
		{
			return OAuthErrors.ApiSecretNotFound(secretId);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<OAuthApiResourceState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return OAuthErrors.ApiResourceNotFound(id);
		}

		var securityData = await session.LoadAsync<OAuthApiResourceSecurityData>(guid, cancellationToken);
		if (securityData is null)
		{
			return OAuthErrors.ApiSecretNotFound(secretId);
		}

		var entry = securityData.Secrets.FirstOrDefault(s => s.SecretId == secretGuid);
		if (entry is null)
		{
			return OAuthErrors.ApiSecretNotFound(secretId);
		}

		securityData.Secrets.Remove(entry);

		// Update the legacy ApiSecret to the most recent remaining secret, or null
		securityData.ApiSecret = securityData.Secrets.LastOrDefault()?.HashedValue;
		securityData.UpdateConcurrencyToken();
		session.Store(securityData);

		await session.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<bool> ValidateCredentialsAsync(
		string name,
		string secret,
		CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenQuerySession();

		var resource = await session.Query<OAuthApiResourceState>()
			.FirstOrDefaultAsync(x => x.Name == name && !x.IsDeleted && x.Enabled, cancellationToken);

		if (resource is null)
		{
			return false;
		}

		var securityData = await session.LoadAsync<OAuthApiResourceSecurityData>(resource.Id, cancellationToken);
		if (securityData is null)
		{
			return false;
		}

		// Check against all secrets in the list (supports key rotation)
		foreach (var entry in securityData.Secrets)
		{
			// Skip expired secrets
			if (entry.Expiration.HasValue && entry.Expiration.Value < DateTimeOffset.UtcNow)
			{
				continue;
			}

			if (VerifySecret(secret, entry.HashedValue))
			{
				return true;
			}
		}

		// Fall back to the legacy single secret field for backward compatibility
		if (securityData.ApiSecret is not null)
		{
			return VerifySecret(secret, securityData.ApiSecret);
		}

		return false;
	}

	private static async Task<OAuthApiResourceDto> MapToDtoAsync(
		IQuerySession session,
		OAuthApiResourceState state,
		CancellationToken cancellationToken)
	{
		var securityData = await session.LoadAsync<OAuthApiResourceSecurityData>(state.Id, cancellationToken);

		var secrets = securityData?.Secrets
			.Select(s => new ApiSecretEntryDto
			{
				SecretId = s.SecretId.ToString(),
				Type = s.Type,
				Description = s.Description,
				Expiration = s.Expiration,
				CreatedAt = s.CreatedAt
			})
			.ToList() ?? new List<ApiSecretEntryDto>();

		return new OAuthApiResourceDto
		{
			Id = state.Id.ToString(),
			Name = state.Name,
			DisplayName = state.DisplayName,
			Description = state.Description,
			Enabled = state.Enabled,
			Scopes = state.Scopes,
			UserClaims = state.UserClaims,
			Secrets = secrets
		};
	}

	private static string GenerateSecret()
	{
		var bytes = new byte[32];
		using var rng = RandomNumberGenerator.Create();
		rng.GetBytes(bytes);
		return Convert.ToBase64String(bytes);
	}

	/// <summary>
	/// Hashes a secret using BCrypt for secure storage.
	/// </summary>
	private static string HashSecret(string secret)
	{
		// Use BCrypt with work factor 12 (same as OpenIddict's default)
		return BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12);
	}

	/// <summary>
	/// Verifies a secret against its BCrypt hash.
	/// </summary>
	private static bool VerifySecret(string providedSecret, string storedHash)
	{
		try
		{
			return BCrypt.Net.BCrypt.Verify(providedSecret, storedHash);
		}
		catch
		{
			return false;
		}
	}
}
