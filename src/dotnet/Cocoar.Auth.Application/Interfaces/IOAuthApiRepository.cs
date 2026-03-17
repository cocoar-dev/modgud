using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for OAuth API operations.
/// </summary>
public interface IOAuthApiRepository
{
	/// <summary>
	/// Gets all APIs with pagination.
	/// </summary>
	Task<OAuthApiListDto> GetAllAsync(
		PaginationRequest pagination,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets an API by ID.
	/// </summary>
	Task<OAuthApiDto?> GetByIdAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new API.
	/// </summary>
	Task<ErrorOr<OAuthApiCreatedDto>> CreateAsync(
		CreateOAuthApiDto dto,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates an existing API.
	/// </summary>
	Task<ErrorOr<OAuthApiDto>> UpdateAsync(
		string id,
		UpdateOAuthApiDto dto,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes an API.
	/// </summary>
	Task<ErrorOr<bool>> DeleteAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Regenerates the API secret for an API (legacy single-secret).
	/// </summary>
	Task<ErrorOr<ApiSecretDto>> RegenerateSecretAsync(
		string id,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new API secret with metadata for an API.
	/// </summary>
	Task<ErrorOr<ApiSecretCreatedDto>> CreateSecretAsync(
		string id,
		CreateApiSecretDto dto,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes a specific API secret by its secret ID.
	/// </summary>
	Task<ErrorOr<bool>> DeleteSecretAsync(
		string id,
		string secretId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Validates an API's credentials for introspection.
	/// </summary>
	Task<bool> ValidateCredentialsAsync(
		string name,
		string secret,
		CancellationToken cancellationToken = default);
}
