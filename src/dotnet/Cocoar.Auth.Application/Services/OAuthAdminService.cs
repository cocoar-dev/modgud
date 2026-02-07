using System.Security.Cryptography;
using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using ErrorOr;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Service for managing OAuth clients, scopes, and API resources.
/// Clients and scopes use OpenIddict managers, API resources use repository.
/// </summary>
public class OAuthAdminService
{
	private readonly IOpenIddictApplicationManager _applicationManager;
	private readonly IOpenIddictScopeManager _scopeManager;
	private readonly IOAuthApiResourceRepository _apiResourceRepository;

	public OAuthAdminService(
		IOpenIddictApplicationManager applicationManager,
		IOpenIddictScopeManager scopeManager,
		IOAuthApiResourceRepository apiResourceRepository)
	{
		_applicationManager = applicationManager;
		_scopeManager = scopeManager;
		_apiResourceRepository = apiResourceRepository;
	}

	#region Clients

	/// <summary>
	/// Gets all OAuth clients with pagination.
	/// </summary>
	public async Task<OAuthClientListDto> GetClientsAsync(
		PaginationRequest pagination,
		CancellationToken cancellationToken = default)
	{
		var clients = new List<OAuthClientDto>();
		var totalCount = 0;

		await foreach (var application in _applicationManager.ListAsync(
			pagination.PageSize,
			(pagination.Page - 1) * pagination.PageSize,
			cancellationToken))
		{
			clients.Add(await MapToClientDtoAsync(application, cancellationToken));
		}

		// Count total clients
		await foreach (var _ in _applicationManager.ListAsync(int.MaxValue, 0, cancellationToken))
		{
			totalCount++;
		}

		return new OAuthClientListDto
		{
			Items = clients,
			TotalCount = totalCount
		};
	}

	/// <summary>
	/// Gets an OAuth client by its internal ID.
	/// </summary>
	public async Task<OAuthClientDto?> GetClientByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		var application = await _applicationManager.FindByIdAsync(id, cancellationToken);
		if (application is null)
		{
			return null;
		}

		return await MapToClientDtoAsync(application, cancellationToken);
	}

	/// <summary>
	/// Creates a new OAuth client.
	/// </summary>
	public async Task<ErrorOr<OAuthClientCreatedDto>> CreateClientAsync(
		CreateOAuthClientDto dto,
		CancellationToken cancellationToken = default)
	{
		// Check if client ID already exists
		var existing = await _applicationManager.FindByClientIdAsync(dto.ClientId, cancellationToken);
		if (existing is not null)
		{
			return OAuthErrors.ClientIdAlreadyExists(dto.ClientId);
		}

		// Validate client type
		if (dto.ClientType != ClientTypes.Public && dto.ClientType != ClientTypes.Confidential)
		{
			return OAuthErrors.InvalidClientType(dto.ClientType);
		}

		// Validate consent type
		if (dto.ConsentType != ConsentTypes.Explicit &&
		    dto.ConsentType != ConsentTypes.Implicit &&
		    dto.ConsentType != ConsentTypes.External)
		{
			return OAuthErrors.InvalidConsentType(dto.ConsentType);
		}

		// Confidential clients must have a secret
		string? clientSecret = null;
		if (dto.ClientType == ClientTypes.Confidential)
		{
			clientSecret = dto.ClientSecret ?? GenerateClientSecret();
		}

		var descriptor = new OpenIddictApplicationDescriptor
		{
			ClientId = dto.ClientId,
			DisplayName = dto.DisplayName,
			ClientType = dto.ClientType,
			ConsentType = dto.ConsentType
		};

		// Add redirect URIs
		foreach (var uri in dto.RedirectUris)
		{
			descriptor.RedirectUris.Add(new Uri(uri));
		}

		// Add post-logout redirect URIs
		foreach (var uri in dto.PostLogoutRedirectUris)
		{
			descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
		}

		// Add standard permissions for authorization code flow
		descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
		descriptor.Permissions.Add(Permissions.Endpoints.Token);
		descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
		descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
		descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
		descriptor.Permissions.Add(Permissions.ResponseTypes.Code);

		// Add scope permissions
		foreach (var scope in dto.Scopes)
		{
			descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
		}

		// Create the application
		// For confidential clients, hash the secret using BCrypt before storing
		if (clientSecret is not null)
		{
			// Hash the secret using BCrypt (compatible with OpenIddict's credential validation)
			descriptor.ClientSecret = HashClientSecret(clientSecret);
		}
		await _applicationManager.CreateAsync(descriptor, cancellationToken);

		// Retrieve the created application by client ID
		var application = await _applicationManager.FindByClientIdAsync(dto.ClientId, cancellationToken);
		var clientDto = await MapToClientDtoAsync(application!, cancellationToken);

		return new OAuthClientCreatedDto
		{
			Client = clientDto,
			ClientSecret = clientSecret
		};
	}

	/// <summary>
	/// Updates an existing OAuth client.
	/// </summary>
	public async Task<ErrorOr<OAuthClientDto>> UpdateClientAsync(
		string id,
		UpdateOAuthClientDto dto,
		CancellationToken cancellationToken = default)
	{
		var application = await _applicationManager.FindByIdAsync(id, cancellationToken);
		if (application is null)
		{
			return OAuthErrors.ClientNotFound(id);
		}

		var descriptor = new OpenIddictApplicationDescriptor();
		await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);

		if (dto.DisplayName is not null)
		{
			descriptor.DisplayName = dto.DisplayName;
		}

		if (dto.ConsentType is not null)
		{
			if (dto.ConsentType != ConsentTypes.Explicit &&
			    dto.ConsentType != ConsentTypes.Implicit &&
			    dto.ConsentType != ConsentTypes.External)
			{
				return OAuthErrors.InvalidConsentType(dto.ConsentType);
			}

			descriptor.ConsentType = dto.ConsentType;
		}

		if (dto.RedirectUris is not null)
		{
			descriptor.RedirectUris.Clear();
			foreach (var uri in dto.RedirectUris)
			{
				descriptor.RedirectUris.Add(new Uri(uri));
			}
		}

		if (dto.PostLogoutRedirectUris is not null)
		{
			descriptor.PostLogoutRedirectUris.Clear();
			foreach (var uri in dto.PostLogoutRedirectUris)
			{
				descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
			}
		}

		if (dto.Scopes is not null)
		{
			// Remove existing scope permissions
			var existingScopes = descriptor.Permissions
				.Where(p => p.StartsWith(Permissions.Prefixes.Scope))
				.ToList();

			foreach (var scope in existingScopes)
			{
				descriptor.Permissions.Remove(scope);
			}

			// Add new scope permissions
			foreach (var scope in dto.Scopes)
			{
				descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
			}
		}

		await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

		return await MapToClientDtoAsync(application, cancellationToken);
	}

	/// <summary>
	/// Deletes an OAuth client.
	/// </summary>
	public async Task<ErrorOr<bool>> DeleteClientAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		var application = await _applicationManager.FindByIdAsync(id, cancellationToken);
		if (application is null)
		{
			return OAuthErrors.ClientNotFound(id);
		}

		await _applicationManager.DeleteAsync(application, cancellationToken);
		return true;
	}

	/// <summary>
	/// Regenerates the client secret for a confidential client.
	/// </summary>
	public async Task<ErrorOr<ClientSecretDto>> RegenerateClientSecretAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		var application = await _applicationManager.FindByIdAsync(id, cancellationToken);
		if (application is null)
		{
			return OAuthErrors.ClientNotFound(id);
		}

		var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);
		if (clientType != ClientTypes.Confidential)
		{
			return OAuthErrors.CannotRegenerateSecretForPublicClient;
		}

		var newSecret = GenerateClientSecret();

		var descriptor = new OpenIddictApplicationDescriptor();
		await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
		// Hash the new secret using BCrypt before storing
		descriptor.ClientSecret = HashClientSecret(newSecret);

		await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

		// Return the raw secret to the caller (only time it's visible)
		return new ClientSecretDto { ClientSecret = newSecret };
	}

	#endregion

	#region Scopes

	/// <summary>
	/// Gets all OAuth scopes.
	/// </summary>
	public async Task<OAuthScopeListDto> GetScopesAsync(CancellationToken cancellationToken = default)
	{
		var scopes = new List<OAuthScopeDto>();

		await foreach (var scope in _scopeManager.ListAsync(int.MaxValue, 0, cancellationToken))
		{
			scopes.Add(await MapToScopeDtoAsync(scope, cancellationToken));
		}

		return new OAuthScopeListDto
		{
			Items = scopes,
			TotalCount = scopes.Count
		};
	}

	/// <summary>
	/// Gets an OAuth scope by its internal ID.
	/// </summary>
	public async Task<OAuthScopeDto?> GetScopeByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		var scope = await _scopeManager.FindByIdAsync(id, cancellationToken);
		if (scope is null)
		{
			return null;
		}

		return await MapToScopeDtoAsync(scope, cancellationToken);
	}

	/// <summary>
	/// Creates a new OAuth scope.
	/// </summary>
	public async Task<ErrorOr<OAuthScopeDto>> CreateScopeAsync(
		CreateOAuthScopeDto dto,
		CancellationToken cancellationToken = default)
	{
		// Check if scope name already exists
		var existing = await _scopeManager.FindByNameAsync(dto.Name, cancellationToken);
		if (existing is not null)
		{
			return OAuthErrors.ScopeNameAlreadyExists(dto.Name);
		}

		var descriptor = new OpenIddictScopeDescriptor
		{
			Name = dto.Name,
			DisplayName = dto.DisplayName,
			Description = dto.Description
		};

		foreach (var resource in dto.Resources)
		{
			descriptor.Resources.Add(resource);
		}

		var scope = await _scopeManager.CreateAsync(descriptor, cancellationToken);

		return await MapToScopeDtoAsync(scope, cancellationToken);
	}

	/// <summary>
	/// Updates an existing OAuth scope.
	/// </summary>
	public async Task<ErrorOr<OAuthScopeDto>> UpdateScopeAsync(
		string id,
		UpdateOAuthScopeDto dto,
		CancellationToken cancellationToken = default)
	{
		var scope = await _scopeManager.FindByIdAsync(id, cancellationToken);
		if (scope is null)
		{
			return OAuthErrors.ScopeNotFound(id);
		}

		// Prevent updating standard OpenID Connect scopes
		var scopeName = await _scopeManager.GetNameAsync(scope, cancellationToken);
		if (IsStandardScope(scopeName))
		{
			return OAuthErrors.CannotModifyStandardScope(scopeName!);
		}

		var descriptor = new OpenIddictScopeDescriptor();
		await _scopeManager.PopulateAsync(descriptor, scope, cancellationToken);

		if (dto.DisplayName is not null)
		{
			descriptor.DisplayName = dto.DisplayName;
		}

		if (dto.Description is not null)
		{
			descriptor.Description = dto.Description;
		}

		if (dto.Resources is not null)
		{
			descriptor.Resources.Clear();
			foreach (var resource in dto.Resources)
			{
				descriptor.Resources.Add(resource);
			}
		}

		await _scopeManager.UpdateAsync(scope, descriptor, cancellationToken);

		return await MapToScopeDtoAsync(scope, cancellationToken);
	}

	/// <summary>
	/// Deletes an OAuth scope.
	/// </summary>
	public async Task<ErrorOr<bool>> DeleteScopeAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		var scope = await _scopeManager.FindByIdAsync(id, cancellationToken);
		if (scope is null)
		{
			return OAuthErrors.ScopeNotFound(id);
		}

		// Prevent deleting standard OpenID Connect scopes
		var scopeName = await _scopeManager.GetNameAsync(scope, cancellationToken);
		if (IsStandardScope(scopeName))
		{
			return OAuthErrors.CannotDeleteStandardScope(scopeName!);
		}

		await _scopeManager.DeleteAsync(scope, cancellationToken);
		return true;
	}

	#endregion

	#region API Resources

	/// <summary>
	/// Gets all API resources with pagination.
	/// </summary>
	public Task<OAuthApiResourceListDto> GetApiResourcesAsync(
		PaginationRequest pagination,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.GetAllAsync(pagination, cancellationToken);

	/// <summary>
	/// Gets an API resource by its internal ID.
	/// </summary>
	public Task<OAuthApiResourceDto?> GetApiResourceByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.GetByIdAsync(id, cancellationToken);

	/// <summary>
	/// Creates a new API resource.
	/// </summary>
	public Task<ErrorOr<OAuthApiResourceCreatedDto>> CreateApiResourceAsync(
		CreateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.CreateAsync(dto, cancellationToken);

	/// <summary>
	/// Updates an existing API resource.
	/// </summary>
	public Task<ErrorOr<OAuthApiResourceDto>> UpdateApiResourceAsync(
		string id,
		UpdateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.UpdateAsync(id, dto, cancellationToken);

	/// <summary>
	/// Deletes an API resource.
	/// </summary>
	public Task<ErrorOr<bool>> DeleteApiResourceAsync(
		string id,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.DeleteAsync(id, cancellationToken);

	/// <summary>
	/// Regenerates the API secret for an API resource.
	/// </summary>
	public Task<ErrorOr<ApiSecretDto>> RegenerateApiSecretAsync(
		string id,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.RegenerateSecretAsync(id, cancellationToken);

	/// <summary>
	/// Validates an API resource's credentials for introspection.
	/// </summary>
	public Task<bool> ValidateApiResourceCredentialsAsync(
		string name,
		string secret,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.ValidateCredentialsAsync(name, secret, cancellationToken);

	#endregion

	#region Private Helpers

	private async Task<OAuthClientDto> MapToClientDtoAsync(
		object application,
		CancellationToken cancellationToken)
	{
		var id = await _applicationManager.GetIdAsync(application, cancellationToken);
		var clientId = await _applicationManager.GetClientIdAsync(application, cancellationToken);
		var displayName = await _applicationManager.GetDisplayNameAsync(application, cancellationToken);
		var clientType = await _applicationManager.GetClientTypeAsync(application, cancellationToken);
		var consentType = await _applicationManager.GetConsentTypeAsync(application, cancellationToken);
		var permissions = await _applicationManager.GetPermissionsAsync(application, cancellationToken);
		var redirectUris = await _applicationManager.GetRedirectUrisAsync(application, cancellationToken);
		var postLogoutRedirectUris = await _applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken);

		return new OAuthClientDto
		{
			Id = id!,
			ClientId = clientId!,
			DisplayName = displayName,
			ClientType = clientType ?? ClientTypes.Public,
			ConsentType = consentType ?? ConsentTypes.Explicit,
			Permissions = permissions.ToList(),
			RedirectUris = redirectUris.ToList(),
			PostLogoutRedirectUris = postLogoutRedirectUris.ToList()
		};
	}

	private async Task<OAuthScopeDto> MapToScopeDtoAsync(
		object scope,
		CancellationToken cancellationToken)
	{
		var id = await _scopeManager.GetIdAsync(scope, cancellationToken);
		var name = await _scopeManager.GetNameAsync(scope, cancellationToken);
		var displayName = await _scopeManager.GetDisplayNameAsync(scope, cancellationToken);
		var description = await _scopeManager.GetDescriptionAsync(scope, cancellationToken);
		var resources = await _scopeManager.GetResourcesAsync(scope, cancellationToken);

		return new OAuthScopeDto
		{
			Id = id!,
			Name = name!,
			DisplayName = displayName,
			Description = description,
			Resources = resources.ToList()
		};
	}

	private static string GenerateClientSecret()
	{
		var bytes = new byte[32];
		using var rng = RandomNumberGenerator.Create();
		rng.GetBytes(bytes);
		return Convert.ToBase64String(bytes);
	}

	/// <summary>
	/// Hashes a client secret using BCrypt.
	/// Compatible with OpenIddict's credential validation.
	/// </summary>
	private static string HashClientSecret(string secret)
	{
		// Use BCrypt with work factor 12 (same as OpenIddict's default)
		return BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12);
	}

	/// <summary>
	/// Verifies a client secret against its BCrypt hash.
	/// </summary>
	public static bool VerifyClientSecret(string secret, string hash)
	{
		try
		{
			return BCrypt.Net.BCrypt.Verify(secret, hash);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsStandardScope(string? scopeName)
	{
		return scopeName is Scopes.OpenId
			or Scopes.Email
			or Scopes.Profile
			or Scopes.Phone
			or Scopes.Address
			or Scopes.OfflineAccess
			or Scopes.Roles;
	}

	#endregion
}
