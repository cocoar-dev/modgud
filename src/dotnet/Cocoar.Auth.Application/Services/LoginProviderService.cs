using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using ErrorOr;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Service for managing login providers.
/// </summary>
public class LoginProviderService
{
	private readonly ILoginProviderRepository _loginProviderRepository;

	public LoginProviderService(ILoginProviderRepository loginProviderRepository)
	{
		_loginProviderRepository = loginProviderRepository;
	}

	/// <summary>
	/// Gets all login providers.
	/// </summary>
	public Task<LoginProviderListDto> GetAllAsync(CancellationToken cancellationToken = default)
		=> _loginProviderRepository.GetAllAsync(cancellationToken);

	/// <summary>
	/// Gets a login provider by ID.
	/// </summary>
	public async Task<ErrorOr<LoginProviderDto>> GetByIdAsync(string id, CancellationToken cancellationToken = default)
	{
		var provider = await _loginProviderRepository.GetByIdAsync(id, cancellationToken);
		if (provider is null)
		{
			return LoginProviderErrors.NotFound(id);
		}

		return provider;
	}

	/// <summary>
	/// Creates a new login provider.
	/// </summary>
	public Task<ErrorOr<LoginProviderDto>> CreateAsync(CreateLoginProviderDto dto, CancellationToken cancellationToken = default)
		=> _loginProviderRepository.CreateAsync(dto, cancellationToken);

	/// <summary>
	/// Updates an existing login provider.
	/// </summary>
	public Task<ErrorOr<LoginProviderDto>> UpdateAsync(string id, UpdateLoginProviderDto dto, CancellationToken cancellationToken = default)
		=> _loginProviderRepository.UpdateAsync(id, dto, cancellationToken);

	/// <summary>
	/// Deletes a login provider.
	/// </summary>
	public Task<ErrorOr<bool>> DeleteAsync(string id, CancellationToken cancellationToken = default)
		=> _loginProviderRepository.DeleteAsync(id, cancellationToken);
}
