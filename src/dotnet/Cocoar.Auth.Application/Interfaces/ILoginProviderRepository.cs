using Cocoar.Auth.Application.DTOs.LoginProviders;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for login provider operations.
/// </summary>
public interface ILoginProviderRepository
{
	/// <summary>
	/// Gets all login providers.
	/// </summary>
	Task<LoginProviderListDto> GetAllAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a login provider by ID.
	/// </summary>
	Task<LoginProviderDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a login provider by name.
	/// </summary>
	Task<LoginProviderDto?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new login provider.
	/// </summary>
	Task<ErrorOr<LoginProviderDto>> CreateAsync(CreateLoginProviderDto dto, CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates an existing login provider.
	/// </summary>
	Task<ErrorOr<LoginProviderDto>> UpdateAsync(string id, UpdateLoginProviderDto dto, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes a login provider.
	/// </summary>
	Task<ErrorOr<bool>> DeleteAsync(string id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Ensures the built-in "Internal" login provider exists.
	/// </summary>
	Task EnsureInternalProviderExistsAsync(CancellationToken cancellationToken = default);
}
