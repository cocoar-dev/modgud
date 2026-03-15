using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Common;
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
			ConsentType = dto.ConsentType,
		};

		// Store settings
		descriptor.Settings[OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.ToString();
		descriptor.Settings[OAuthApplicationSettingKeys.RefreshTokenUsage] = dto.RefreshTokenUsage.ToString();

		if (dto.IdentityTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
		if (dto.AccessTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
		if (dto.AuthorizationCodeLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
		if (dto.AbsoluteRefreshTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = dto.AbsoluteRefreshTokenLifetime.Value.ToString();
		if (dto.SlidingRefreshTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
		if (dto.ClientClaimsPrefix is not null)
			descriptor.Settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;

		// Store properties (booleans and complex types as JSON elements)
		SetClientProperties(descriptor, dto.Enabled, dto.AllowAccessTokensViaBrowser,
			dto.RequireClientSecret, dto.EnableLocalLogin, dto.RequireConsent,
			dto.AllowRememberConsent, dto.AllowedCorsOrigins, dto.AlwaysSendClientClaims,
			dto.UpdateAccessTokenClaimsOnRefresh, dto.Claims, dto.Roles);

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

		// Add standard endpoint permissions
		descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
		descriptor.Permissions.Add(Permissions.Endpoints.Token);
		descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
		descriptor.Permissions.Add(Permissions.Endpoints.Introspection);
		descriptor.Permissions.Add(Permissions.Endpoints.Revocation);

		// Add grant type permissions - use explicit list if provided, otherwise defaults
		if (dto.AllowedGrantTypes.Count > 0)
		{
			foreach (var grantType in dto.AllowedGrantTypes)
			{
				var permission = MapGrantTypeToPermission(grantType);
				if (permission is not null)
					descriptor.Permissions.Add(permission);
			}

			// Add response types based on grant types
			if (dto.AllowedGrantTypes.Contains("authorization_code"))
				descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
		}
		else
		{
			// Default grant types
			descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
			descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
			descriptor.Permissions.Add(Permissions.ResponseTypes.Code);

			// Confidential clients can also use client credentials flow
			if (dto.ClientType == ClientTypes.Confidential)
			{
				descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
			}
		}

		// Add scope permissions
		foreach (var scope in dto.Scopes)
		{
			descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
		}

		// Create the application
		// For confidential clients, set the raw secret — OpenIddict will hash it via ObfuscateClientSecretAsync
		if (clientSecret is not null)
		{
			descriptor.ClientSecret = clientSecret;
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

		// Set access token type if provided (stored in Settings dictionary)
		if (dto.AccessTokenType.HasValue)
		{
			descriptor.Settings[OAuthApplicationSettingKeys.AccessTokenType] = dto.AccessTokenType.Value.ToString();
		}

		// Set refresh token usage if provided
		if (dto.RefreshTokenUsage.HasValue)
		{
			descriptor.Settings[OAuthApplicationSettingKeys.RefreshTokenUsage] = dto.RefreshTokenUsage.Value.ToString();
		}

		// Set token lifetime settings if provided
		if (dto.IdentityTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.IdentityTokenLifetime] = dto.IdentityTokenLifetime.Value.ToString();
		if (dto.AccessTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AccessTokenLifetime] = dto.AccessTokenLifetime.Value.ToString();
		if (dto.AuthorizationCodeLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AuthorizationCodeLifetime] = dto.AuthorizationCodeLifetime.Value.ToString();
		if (dto.AbsoluteRefreshTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime] = dto.AbsoluteRefreshTokenLifetime.Value.ToString();
		if (dto.SlidingRefreshTokenLifetime.HasValue)
			descriptor.Settings[OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime] = dto.SlidingRefreshTokenLifetime.Value.ToString();
		if (dto.ClientClaimsPrefix is not null)
			descriptor.Settings[OAuthApplicationSettingKeys.ClientClaimsPrefix] = dto.ClientClaimsPrefix;

		// Update grant type permissions if provided
		if (dto.AllowedGrantTypes is not null)
		{
			// Remove existing grant type and response type permissions
			var existingGrantPerms = descriptor.Permissions
				.Where(p => p.StartsWith(Permissions.Prefixes.GrantType) || p.StartsWith(Permissions.Prefixes.ResponseType))
				.ToList();
			foreach (var perm in existingGrantPerms)
				descriptor.Permissions.Remove(perm);

			foreach (var grantType in dto.AllowedGrantTypes)
			{
				var permission = MapGrantTypeToPermission(grantType);
				if (permission is not null)
					descriptor.Permissions.Add(permission);
			}

			if (dto.AllowedGrantTypes.Contains("authorization_code"))
				descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
		}

		// Update properties (booleans and complex types)
		var currentProperties = await _applicationManager.GetPropertiesAsync(application, cancellationToken);
		var enabled = dto.Enabled ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.Enabled, true);
		var allowBrowser = dto.AllowAccessTokensViaBrowser ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false);
		var requireSecret = dto.RequireClientSecret ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.RequireClientSecret, true);
		var enableLocal = dto.EnableLocalLogin ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.EnableLocalLogin, true);
		var requireConsent = dto.RequireConsent ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.RequireConsent, false);
		var allowRemember = dto.AllowRememberConsent ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.AllowRememberConsent, true);
		var corsOrigins = dto.AllowedCorsOrigins ?? GetStringListProperty(currentProperties, OAuthApplicationPropertyKeys.AllowedCorsOrigins);
		var alwaysSend = dto.AlwaysSendClientClaims ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false);
		var updateClaims = dto.UpdateAccessTokenClaimsOnRefresh ?? GetBoolProperty(currentProperties, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false);
		var claims = dto.Claims?.Select(c => new OAuthClientClaimDto { Type = c.Type, Value = c.Value }).ToList()
			?? GetClientClaimsProperty(currentProperties);
		var roles = dto.Roles ?? GetStringListProperty(currentProperties, OAuthApplicationPropertyKeys.Roles);

		SetClientProperties(descriptor, enabled, allowBrowser, requireSecret, enableLocal,
			requireConsent, allowRemember, corsOrigins, alwaysSend, updateClaims, claims, roles);

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
		// Set the raw secret — OpenIddict will hash it via ObfuscateClientSecretAsync
		descriptor.ClientSecret = newSecret;

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

		// Store identity resource properties in the Properties dictionary
		SetScopeProperties(descriptor, dto.Enabled, dto.Required, dto.Emphasize,
			dto.ShowInDiscoveryDocument, dto.UserClaims);

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

		// Read current identity resource properties and apply updates
		var currentEnabled = GetBoolProperty(descriptor.Properties, ScopePropertyKeys.Enabled, true);
		var currentRequired = GetBoolProperty(descriptor.Properties, ScopePropertyKeys.Required, false);
		var currentEmphasize = GetBoolProperty(descriptor.Properties, ScopePropertyKeys.Emphasize, false);
		var currentShowInDiscovery = GetBoolProperty(descriptor.Properties, ScopePropertyKeys.ShowInDiscoveryDocument, true);
		var currentUserClaims = GetStringListProperty(descriptor.Properties, ScopePropertyKeys.UserClaims);

		SetScopeProperties(descriptor,
			dto.Enabled ?? currentEnabled,
			dto.Required ?? currentRequired,
			dto.Emphasize ?? currentEmphasize,
			dto.ShowInDiscoveryDocument ?? currentShowInDiscovery,
			dto.UserClaims ?? currentUserClaims);

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
	/// Creates a new API secret for an API resource.
	/// </summary>
	public Task<ErrorOr<ApiSecretCreatedDto>> CreateApiSecretAsync(
		string id,
		CreateApiSecretDto dto,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.CreateSecretAsync(id, dto, cancellationToken);

	/// <summary>
	/// Deletes a specific API secret from an API resource.
	/// </summary>
	public Task<ErrorOr<bool>> DeleteApiSecretAsync(
		string id,
		string secretId,
		CancellationToken cancellationToken = default)
		=> _apiResourceRepository.DeleteSecretAsync(id, secretId, cancellationToken);

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
		var settings = await _applicationManager.GetSettingsAsync(application, cancellationToken);
		var properties = await _applicationManager.GetPropertiesAsync(application, cancellationToken);

		// Read settings (simple string values)
		var accessTokenType = AccessTokenType.Reference;
		if (settings.TryGetValue(OAuthApplicationSettingKeys.AccessTokenType, out var tokenTypeValue)
			&& Enum.TryParse<AccessTokenType>(tokenTypeValue, out var parsedTokenType))
			accessTokenType = parsedTokenType;

		var refreshTokenUsage = RefreshTokenUsage.OneTimeOnly;
		if (settings.TryGetValue(OAuthApplicationSettingKeys.RefreshTokenUsage, out var rtuValue)
			&& Enum.TryParse<RefreshTokenUsage>(rtuValue, out var parsedRtu))
			refreshTokenUsage = parsedRtu;

		int? identityTokenLifetime = GetIntSetting(settings, OAuthApplicationSettingKeys.IdentityTokenLifetime);
		int? accessTokenLifetime = GetIntSetting(settings, OAuthApplicationSettingKeys.AccessTokenLifetime);
		int? authorizationCodeLifetime = GetIntSetting(settings, OAuthApplicationSettingKeys.AuthorizationCodeLifetime);
		int? absoluteRefreshTokenLifetime = GetIntSetting(settings, OAuthApplicationSettingKeys.AbsoluteRefreshTokenLifetime);
		int? slidingRefreshTokenLifetime = GetIntSetting(settings, OAuthApplicationSettingKeys.SlidingRefreshTokenLifetime);

		settings.TryGetValue(OAuthApplicationSettingKeys.ClientClaimsPrefix, out var clientClaimsPrefix);

		// Extract grant types from permissions
		var allowedGrantTypes = permissions
			.Where(p => p.StartsWith(Permissions.Prefixes.GrantType))
			.Select(p => MapPermissionToGrantType(p))
			.Where(g => g is not null)
			.Select(g => g!)
			.ToList();

		return new OAuthClientDto
		{
			Id = id!,
			ClientId = clientId!,
			DisplayName = displayName,
			ClientType = clientType ?? ClientTypes.Public,
			ConsentType = consentType ?? ConsentTypes.Explicit,
			Permissions = permissions.ToList(),
			RedirectUris = redirectUris.ToList(),
			PostLogoutRedirectUris = postLogoutRedirectUris.ToList(),
			AccessTokenType = accessTokenType,
			Enabled = GetBoolProperty(properties, OAuthApplicationPropertyKeys.Enabled, true),
			RefreshTokenUsage = refreshTokenUsage,
			AllowAccessTokensViaBrowser = GetBoolProperty(properties, OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser, false),
			RequireClientSecret = GetBoolProperty(properties, OAuthApplicationPropertyKeys.RequireClientSecret, true),
			EnableLocalLogin = GetBoolProperty(properties, OAuthApplicationPropertyKeys.EnableLocalLogin, true),
			RequireConsent = GetBoolProperty(properties, OAuthApplicationPropertyKeys.RequireConsent, false),
			AllowRememberConsent = GetBoolProperty(properties, OAuthApplicationPropertyKeys.AllowRememberConsent, true),
			AllowedGrantTypes = allowedGrantTypes,
			AllowedCorsOrigins = GetStringListProperty(properties, OAuthApplicationPropertyKeys.AllowedCorsOrigins),
			IdentityTokenLifetime = identityTokenLifetime,
			AccessTokenLifetime = accessTokenLifetime,
			AuthorizationCodeLifetime = authorizationCodeLifetime,
			AbsoluteRefreshTokenLifetime = absoluteRefreshTokenLifetime,
			SlidingRefreshTokenLifetime = slidingRefreshTokenLifetime,
			AlwaysSendClientClaims = GetBoolProperty(properties, OAuthApplicationPropertyKeys.AlwaysSendClientClaims, false),
			UpdateAccessTokenClaimsOnRefresh = GetBoolProperty(properties, OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh, false),
			ClientClaimsPrefix = clientClaimsPrefix,
			Claims = GetClientClaimsProperty(properties),
			Roles = GetStringListProperty(properties, OAuthApplicationPropertyKeys.Roles)
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
		var properties = await _scopeManager.GetPropertiesAsync(scope, cancellationToken);

		return new OAuthScopeDto
		{
			Id = id!,
			Name = name!,
			DisplayName = displayName,
			Description = description,
			Resources = resources.ToList(),
			Enabled = GetBoolProperty(properties, ScopePropertyKeys.Enabled, true),
			Required = GetBoolProperty(properties, ScopePropertyKeys.Required, false),
			Emphasize = GetBoolProperty(properties, ScopePropertyKeys.Emphasize, false),
			ShowInDiscoveryDocument = GetBoolProperty(properties, ScopePropertyKeys.ShowInDiscoveryDocument, true),
			UserClaims = GetStringListProperty(properties, ScopePropertyKeys.UserClaims)
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

	private static void SetScopeProperties(
		OpenIddictScopeDescriptor descriptor,
		bool enabled, bool required, bool emphasize,
		bool showInDiscoveryDocument, IReadOnlyList<string> userClaims)
	{
		descriptor.Properties[ScopePropertyKeys.Enabled] = JsonSerializer.SerializeToElement(enabled);
		descriptor.Properties[ScopePropertyKeys.Required] = JsonSerializer.SerializeToElement(required);
		descriptor.Properties[ScopePropertyKeys.Emphasize] = JsonSerializer.SerializeToElement(emphasize);
		descriptor.Properties[ScopePropertyKeys.ShowInDiscoveryDocument] = JsonSerializer.SerializeToElement(showInDiscoveryDocument);
		descriptor.Properties[ScopePropertyKeys.UserClaims] = JsonSerializer.SerializeToElement(userClaims);
	}

	private static bool GetBoolProperty(
		ImmutableDictionary<string, JsonElement> properties,
		string key, bool defaultValue)
	{
		if (properties.TryGetValue(key, out var element) &&
		    element.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			return element.GetBoolean();
		}
		return defaultValue;
	}

	private static bool GetBoolProperty(
		IDictionary<string, JsonElement> properties,
		string key, bool defaultValue)
	{
		if (properties.TryGetValue(key, out var element) &&
		    element.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			return element.GetBoolean();
		}
		return defaultValue;
	}

	private static List<string> GetStringListProperty(
		ImmutableDictionary<string, JsonElement> properties,
		string key)
	{
		if (properties.TryGetValue(key, out var element) &&
		    element.ValueKind == JsonValueKind.Array)
		{
			return element.EnumerateArray()
				.Where(e => e.ValueKind == JsonValueKind.String)
				.Select(e => e.GetString()!)
				.ToList();
		}
		return [];
	}

	private static List<string> GetStringListProperty(
		IDictionary<string, JsonElement> properties,
		string key)
	{
		if (properties.TryGetValue(key, out var element) &&
		    element.ValueKind == JsonValueKind.Array)
		{
			return element.EnumerateArray()
				.Where(e => e.ValueKind == JsonValueKind.String)
				.Select(e => e.GetString()!)
				.ToList();
		}
		return [];
	}

	private static int? GetIntSetting(ImmutableDictionary<string, string> settings, string key)
	{
		if (settings.TryGetValue(key, out var value) && int.TryParse(value, out var result))
			return result;
		return null;
	}

	private static List<OAuthClientClaimDto> GetClientClaimsProperty(
		ImmutableDictionary<string, JsonElement> properties)
	{
		if (properties.TryGetValue(OAuthApplicationPropertyKeys.ClientClaims, out var element) &&
		    element.ValueKind == JsonValueKind.Array)
		{
			var claims = new List<OAuthClientClaimDto>();
			foreach (var item in element.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object &&
				    item.TryGetProperty("Type", out var typeProp) &&
				    item.TryGetProperty("Value", out var valueProp) &&
				    typeProp.ValueKind == JsonValueKind.String &&
				    valueProp.ValueKind == JsonValueKind.String)
				{
					claims.Add(new OAuthClientClaimDto
					{
						Type = typeProp.GetString()!,
						Value = valueProp.GetString()!
					});
				}
			}
			return claims;
		}
		return [];
	}

	private static void SetClientProperties(
		OpenIddictApplicationDescriptor descriptor,
		bool enabled, bool allowAccessTokensViaBrowser, bool requireClientSecret,
		bool enableLocalLogin, bool requireConsent, bool allowRememberConsent,
		IReadOnlyList<string> allowedCorsOrigins, bool alwaysSendClientClaims,
		bool updateAccessTokenClaimsOnRefresh, IReadOnlyList<OAuthClientClaimDto> claims,
		IReadOnlyList<string> roles)
	{
		descriptor.Properties[OAuthApplicationPropertyKeys.Enabled] = JsonSerializer.SerializeToElement(enabled);
		descriptor.Properties[OAuthApplicationPropertyKeys.AllowAccessTokensViaBrowser] = JsonSerializer.SerializeToElement(allowAccessTokensViaBrowser);
		descriptor.Properties[OAuthApplicationPropertyKeys.RequireClientSecret] = JsonSerializer.SerializeToElement(requireClientSecret);
		descriptor.Properties[OAuthApplicationPropertyKeys.EnableLocalLogin] = JsonSerializer.SerializeToElement(enableLocalLogin);
		descriptor.Properties[OAuthApplicationPropertyKeys.RequireConsent] = JsonSerializer.SerializeToElement(requireConsent);
		descriptor.Properties[OAuthApplicationPropertyKeys.AllowRememberConsent] = JsonSerializer.SerializeToElement(allowRememberConsent);
		descriptor.Properties[OAuthApplicationPropertyKeys.AllowedCorsOrigins] = JsonSerializer.SerializeToElement(allowedCorsOrigins);
		descriptor.Properties[OAuthApplicationPropertyKeys.AlwaysSendClientClaims] = JsonSerializer.SerializeToElement(alwaysSendClientClaims);
		descriptor.Properties[OAuthApplicationPropertyKeys.UpdateAccessTokenClaimsOnRefresh] = JsonSerializer.SerializeToElement(updateAccessTokenClaimsOnRefresh);
		descriptor.Properties[OAuthApplicationPropertyKeys.ClientClaims] = JsonSerializer.SerializeToElement(claims);
		descriptor.Properties[OAuthApplicationPropertyKeys.Roles] = JsonSerializer.SerializeToElement(roles);
	}

	private static string? MapGrantTypeToPermission(string grantType)
	{
		return grantType switch
		{
			"authorization_code" => Permissions.GrantTypes.AuthorizationCode,
			"client_credentials" => Permissions.GrantTypes.ClientCredentials,
			"refresh_token" => Permissions.GrantTypes.RefreshToken,
			"implicit" => Permissions.GrantTypes.Implicit,
			"password" => Permissions.GrantTypes.Password,
			"urn:ietf:params:oauth:grant-type:device_code" => Permissions.GrantTypes.DeviceCode,
			_ => null
		};
	}

	private static string? MapPermissionToGrantType(string permission)
	{
		return permission switch
		{
			Permissions.GrantTypes.AuthorizationCode => "authorization_code",
			Permissions.GrantTypes.ClientCredentials => "client_credentials",
			Permissions.GrantTypes.RefreshToken => "refresh_token",
			Permissions.GrantTypes.Implicit => "implicit",
			Permissions.GrantTypes.Password => "password",
			Permissions.GrantTypes.DeviceCode => "urn:ietf:params:oauth:grant-type:device_code",
			_ => null
		};
	}

	#endregion
}
