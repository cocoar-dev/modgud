using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Admin controller for managing OAuth clients and scopes.
/// </summary>
[Route("api/admin/oauth")]
[Authorize(Roles = "Admin")]
public class OAuthAdminController : ApiControllerBase
{
	private readonly OAuthAdminService _oAuthAdminService;

	public OAuthAdminController(OAuthAdminService oAuthAdminService)
	{
		_oAuthAdminService = oAuthAdminService;
	}

	#region Clients

	/// <summary>
	/// Get all OAuth clients with pagination.
	/// </summary>
	[HttpGet("clients")]
	[ProducesResponseType(typeof(OAuthClientListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetClients(
		[FromQuery] int page = 1,
		[FromQuery] int pageSize = 20,
		CancellationToken cancellationToken = default)
	{
		var pagination = new PaginationRequest { Page = page, PageSize = pageSize };
		var result = await _oAuthAdminService.GetClientsAsync(pagination, cancellationToken);
		return Ok(result);
	}

	/// <summary>
	/// Get an OAuth client by ID.
	/// </summary>
	[HttpGet("clients/{id}")]
	[ProducesResponseType(typeof(OAuthClientDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetClient(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.GetClientByIdAsync(id, cancellationToken);
		if (result is null)
		{
			return NotFound();
		}

		return Ok(result);
	}

	/// <summary>
	/// Create a new OAuth client.
	/// </summary>
	[HttpPost("clients")]
	[ProducesResponseType(typeof(OAuthClientCreatedDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateClient(
		[FromBody] CreateOAuthClientDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.CreateClientAsync(dto, cancellationToken);

		return result.Match(
			created => CreatedAtAction(
				nameof(GetClient),
				new { id = created.Client.Id },
				created),
			errors => Problem(errors));
	}

	/// <summary>
	/// Update an existing OAuth client.
	/// </summary>
	[HttpPut("clients/{id}")]
	[ProducesResponseType(typeof(OAuthClientDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateClient(
		string id,
		[FromBody] UpdateOAuthClientDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.UpdateClientAsync(id, dto, cancellationToken);

		return result.Match(
			client => Ok(client),
			errors => Problem(errors));
	}

	/// <summary>
	/// Delete an OAuth client.
	/// </summary>
	[HttpDelete("clients/{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteClient(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.DeleteClientAsync(id, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}

	/// <summary>
	/// Regenerate the client secret for a confidential client.
	/// </summary>
	[HttpPost("clients/{id}/regenerate-secret")]
	[ProducesResponseType(typeof(ClientSecretDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> RegenerateClientSecret(
		string id,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.RegenerateClientSecretAsync(id, cancellationToken);

		return result.Match(
			secret => Ok(secret),
			errors => Problem(errors));
	}

	#endregion

	#region Scopes

	/// <summary>
	/// Get all OAuth scopes.
	/// </summary>
	[HttpGet("scopes")]
	[ProducesResponseType(typeof(OAuthScopeListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetScopes(CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.GetScopesAsync(cancellationToken);
		return Ok(result);
	}

	/// <summary>
	/// Get an OAuth scope by ID.
	/// </summary>
	[HttpGet("scopes/{id}")]
	[ProducesResponseType(typeof(OAuthScopeDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetScope(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.GetScopeByIdAsync(id, cancellationToken);
		if (result is null)
		{
			return NotFound();
		}

		return Ok(result);
	}

	/// <summary>
	/// Create a new OAuth scope.
	/// </summary>
	[HttpPost("scopes")]
	[ProducesResponseType(typeof(OAuthScopeDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateScope(
		[FromBody] CreateOAuthScopeDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.CreateScopeAsync(dto, cancellationToken);

		return result.Match(
			scope => CreatedAtAction(nameof(GetScope), new { id = scope.Id }, scope),
			errors => Problem(errors));
	}

	/// <summary>
	/// Update an existing OAuth scope.
	/// </summary>
	[HttpPut("scopes/{id}")]
	[ProducesResponseType(typeof(OAuthScopeDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateScope(
		string id,
		[FromBody] UpdateOAuthScopeDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.UpdateScopeAsync(id, dto, cancellationToken);

		return result.Match(
			scope => Ok(scope),
			errors => Problem(errors));
	}

	/// <summary>
	/// Delete an OAuth scope.
	/// </summary>
	[HttpDelete("scopes/{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteScope(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.DeleteScopeAsync(id, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}

	#endregion

	#region API Resources

	/// <summary>
	/// Get all OAuth API resources with pagination.
	/// </summary>
	[HttpGet("api-resources")]
	[ProducesResponseType(typeof(OAuthApiResourceListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetApiResources(
		[FromQuery] int page = 1,
		[FromQuery] int pageSize = 20,
		CancellationToken cancellationToken = default)
	{
		var pagination = new PaginationRequest { Page = page, PageSize = pageSize };
		var result = await _oAuthAdminService.GetApiResourcesAsync(pagination, cancellationToken);
		return Ok(result);
	}

	/// <summary>
	/// Get an OAuth API resource by ID.
	/// </summary>
	[HttpGet("api-resources/{id}")]
	[ProducesResponseType(typeof(OAuthApiResourceDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetApiResource(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.GetApiResourceByIdAsync(id, cancellationToken);
		if (result is null)
		{
			return NotFound();
		}

		return Ok(result);
	}

	/// <summary>
	/// Create a new OAuth API resource.
	/// </summary>
	[HttpPost("api-resources")]
	[ProducesResponseType(typeof(OAuthApiResourceCreatedDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateApiResource(
		[FromBody] CreateOAuthApiResourceDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.CreateApiResourceAsync(dto, cancellationToken);

		return result.Match(
			created => CreatedAtAction(
				nameof(GetApiResource),
				new { id = created.Id },
				created),
			errors => Problem(errors));
	}

	/// <summary>
	/// Update an existing OAuth API resource.
	/// </summary>
	[HttpPut("api-resources/{id}")]
	[ProducesResponseType(typeof(OAuthApiResourceDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateApiResource(
		string id,
		[FromBody] UpdateOAuthApiResourceDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.UpdateApiResourceAsync(id, dto, cancellationToken);

		return result.Match(
			apiResource => Ok(apiResource),
			errors => Problem(errors));
	}

	/// <summary>
	/// Delete an OAuth API resource.
	/// </summary>
	[HttpDelete("api-resources/{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteApiResource(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.DeleteApiResourceAsync(id, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}

	/// <summary>
	/// Regenerate the API secret for an API resource.
	/// </summary>
	[HttpPost("api-resources/{id}/regenerate-secret")]
	[ProducesResponseType(typeof(ApiSecretDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> RegenerateApiSecret(
		string id,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.RegenerateApiSecretAsync(id, cancellationToken);

		return result.Match(
			secret => Ok(secret),
			errors => Problem(errors));
	}

	#endregion
}
