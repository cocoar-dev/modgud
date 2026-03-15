using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Admin controller for managing login providers.
/// </summary>
[Route("api/admin/login-providers")]
[Authorize(Roles = "Admin")]
public class LoginProvidersAdminController : ApiControllerBase
{
	private readonly LoginProviderService _loginProviderService;

	public LoginProvidersAdminController(LoginProviderService loginProviderService)
	{
		_loginProviderService = loginProviderService;
	}

	/// <summary>
	/// Get all login providers.
	/// </summary>
	[HttpGet]
	[ProducesResponseType(typeof(LoginProviderListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetLoginProviders(CancellationToken cancellationToken)
	{
		var result = await _loginProviderService.GetAllAsync(cancellationToken);
		return Ok(result);
	}

	/// <summary>
	/// Get a login provider by ID.
	/// </summary>
	[HttpGet("{id}")]
	[ProducesResponseType(typeof(LoginProviderDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetLoginProvider(string id, CancellationToken cancellationToken)
	{
		var result = await _loginProviderService.GetByIdAsync(id, cancellationToken);

		return result.Match(
			provider => Ok(provider),
			errors => Problem(errors));
	}

	/// <summary>
	/// Create a new login provider.
	/// </summary>
	[HttpPost]
	[ProducesResponseType(typeof(LoginProviderDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateLoginProvider(
		[FromBody] CreateLoginProviderDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _loginProviderService.CreateAsync(dto, cancellationToken);

		return result.Match(
			provider => CreatedAtAction(
				nameof(GetLoginProvider),
				new { id = provider.Id },
				provider),
			errors => Problem(errors));
	}

	/// <summary>
	/// Update an existing login provider.
	/// </summary>
	[HttpPatch("{id}")]
	[ProducesResponseType(typeof(LoginProviderDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> UpdateLoginProvider(
		string id,
		[FromBody] UpdateLoginProviderDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _loginProviderService.UpdateAsync(id, dto, cancellationToken);

		return result.Match(
			provider => Ok(provider),
			errors => Problem(errors));
	}

	/// <summary>
	/// Delete a login provider.
	/// </summary>
	[HttpDelete("{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteLoginProvider(string id, CancellationToken cancellationToken)
	{
		var result = await _loginProviderService.DeleteAsync(id, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}
}
