using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for OAuth API resource operations.
/// </summary>
public interface IOAuthApiResourceRepository
{
	/// <summary>
	/// Gets all API resources with pagination.
	/// </summary>
	Task<OAuthApiResourceListDto> GetAllAsync(
		PaginationRequest pagination,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets an API resource by ID.
	/// </summary>
	Task<OAuthApiResourceDto?> GetByIdAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new API resource.
	/// </summary>
	Task<ErrorOr<OAuthApiResourceCreatedDto>> CreateAsync(
		CreateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates an existing API resource.
	/// </summary>
	Task<ErrorOr<OAuthApiResourceDto>> UpdateAsync(
		string id,
		UpdateOAuthApiResourceDto dto,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes an API resource.
	/// </summary>
	Task<ErrorOr<bool>> DeleteAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Regenerates the API secret for an API resource.
	/// </summary>
	Task<ErrorOr<ApiSecretDto>> RegenerateSecretAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Validates an API resource's credentials for introspection.
	/// </summary>
	Task<bool> ValidateCredentialsAsync(
		string name,
		string secret,
		CancellationToken cancellationToken = default);
}
