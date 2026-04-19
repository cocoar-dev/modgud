using Cocoar.Auth.Api.Authorization;
using Cocoar.Auth.Application.DTOs.Common;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Cocoar.Auth.Api.Hubs;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Admin controller for managing OAuth clients and scopes.
/// </summary>
[Route("api/admin/oauth")]
[RequiresAbacPermission("tenant:admin")]
public class OAuthAdminController : ApiControllerBase
{
	private readonly OAuthAdminService _oAuthAdminService;
	private readonly IAdminHubNotifier _hubNotifier;

	public OAuthAdminController(OAuthAdminService oAuthAdminService, IAdminHubNotifier hubNotifier)
	{
		_oAuthAdminService = oAuthAdminService;
		_hubNotifier = hubNotifier;
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

		if (result.IsError) return Problem(result.Errors);

		var created = result.Value;
		await _hubNotifier.EntityChangedAsync("oauth-client", "created", created.Client.Id);
		return CreatedAtAction(nameof(GetClient), new { id = created.Client.Id }, created);
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

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("oauth-client", "updated", id);
		return Ok(result.Value);
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

		await _hubNotifier.EntityChangedAsync("oauth-client", "deleted", id);
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

		if (result.IsError) return Problem(result.Errors);

		var scope = result.Value;
		await _hubNotifier.EntityChangedAsync("oauth-scope", "created", scope.Id);
		return CreatedAtAction(nameof(GetScope), new { id = scope.Id }, scope);
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

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("oauth-scope", "updated", id);
		return Ok(result.Value);
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

		await _hubNotifier.EntityChangedAsync("oauth-scope", "deleted", id);
		return NoContent();
	}

	#endregion

	#region APIs

	/// <summary>
	/// Get all OAuth APIs with pagination.
	/// </summary>
	[HttpGet("apis")]
	[ProducesResponseType(typeof(OAuthApiListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetApis(
		[FromQuery] int page = 1,
		[FromQuery] int pageSize = 20,
		CancellationToken cancellationToken = default)
	{
		var pagination = new PaginationRequest { Page = page, PageSize = pageSize };
		var result = await _oAuthAdminService.GetApisAsync(pagination, cancellationToken);
		return Ok(result);
	}

	/// <summary>
	/// Get an OAuth API by ID.
	/// </summary>
	[HttpGet("apis/{id}")]
	[ProducesResponseType(typeof(OAuthApiDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetApi(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.GetApiByIdAsync(id, cancellationToken);
		if (result is null)
		{
			return NotFound();
		}

		return Ok(result);
	}

	/// <summary>
	/// Create a new OAuth API.
	/// </summary>
	[HttpPost("apis")]
	[ProducesResponseType(typeof(OAuthApiCreatedDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateApi(
		[FromBody] CreateOAuthApiDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.CreateApiAsync(dto, cancellationToken);

		if (result.IsError) return Problem(result.Errors);

		var created = result.Value;
		await _hubNotifier.EntityChangedAsync("oauth-api", "created", created.Id);
		return CreatedAtAction(nameof(GetApi), new { id = created.Id }, created);
	}

	/// <summary>
	/// Update an existing OAuth API.
	/// </summary>
	[HttpPut("apis/{id}")]
	[ProducesResponseType(typeof(OAuthApiDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateApi(
		string id,
		[FromBody] UpdateOAuthApiDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.UpdateApiAsync(id, dto, cancellationToken);

		if (result.IsError) return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("oauth-api", "updated", id);
		return Ok(result.Value);
	}

	/// <summary>
	/// Delete an OAuth API.
	/// </summary>
	[HttpDelete("apis/{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteApi(string id, CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.DeleteApiAsync(id, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		await _hubNotifier.EntityChangedAsync("oauth-api", "deleted", id);
		return NoContent();
	}

	/// <summary>
	/// Regenerate the API secret for an API.
	/// </summary>
	[HttpPost("apis/{id}/regenerate-secret")]
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

	/// <summary>
	/// Create a new API secret for an API.
	/// Returns the plaintext secret only once.
	/// </summary>
	[HttpPost("apis/{id}/secrets")]
	[ProducesResponseType(typeof(ApiSecretCreatedDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> CreateApiSecret(
		string id,
		[FromBody] CreateApiSecretDto dto,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.CreateApiSecretAsync(id, dto, cancellationToken);

		return result.Match(
			created => StatusCode(StatusCodes.Status201Created, created),
			errors => Problem(errors));
	}

	/// <summary>
	/// Delete a specific API secret from an API.
	/// </summary>
	[HttpDelete("apis/{id}/secrets/{secretId}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteApiSecret(
		string id,
		string secretId,
		CancellationToken cancellationToken)
	{
		var result = await _oAuthAdminService.DeleteApiSecretAsync(id, secretId, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}

	#endregion
}
