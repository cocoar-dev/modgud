using Cocoar.Auth.Api.Filters;
using Cocoar.Auth.Api.Hubs;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Realm management API. Only accessible from tenants with CanManageTenants enabled.
/// </summary>
[Route("api/admin/realms")]
[Authorize(Roles = "Admin")]
[CanManageTenants]
public class RealmsAdminController : ApiControllerBase
{
	private readonly IRealmProvisioningService _realmService;
	private readonly IAdminHubNotifier _hubNotifier;

	public RealmsAdminController(IRealmProvisioningService realmService, IAdminHubNotifier hubNotifier)
	{
		_realmService = realmService;
		_hubNotifier = hubNotifier;
	}

	[HttpGet]
	[ProducesResponseType(typeof(RealmListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetRealms(CancellationToken cancellationToken)
	{
		var realms = await _realmService.GetAllRealmsAsync(cancellationToken);

		var items = new List<RealmDto>();
		foreach (var realm in realms)
		{
			items.Add(MapToDto(realm, await _realmService.NeedsSetupAsync(realm.Slug, cancellationToken)));
		}

		return Ok(new RealmListDto
		{
			Items = items,
			TotalCount = items.Count
		});
	}

	[HttpGet("{slug}")]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetRealm(string slug, CancellationToken cancellationToken)
	{
		var realm = await _realmService.GetRealmBySlugAsync(slug, cancellationToken);
		if (realm is null)
			return NotFound();

		return Ok(MapToDto(realm, await _realmService.NeedsSetupAsync(realm.Slug, cancellationToken)));
	}

	[HttpPost]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateRealm([FromBody] CreateRealmDto dto, CancellationToken cancellationToken)
	{
		var result = await _realmService.CreateRealmAsync(dto, cancellationToken);
		if (result.IsError)
			return Problem(result.Errors);

		var realm = result.Value;
		await _hubNotifier.EntityChangedAsync("realm", "created", realm.Slug);
		return CreatedAtAction(nameof(GetRealm), new { slug = realm.Slug }, MapToDto(realm, needsSetup: true));
	}

	[HttpPatch("{slug}")]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateRealm(string slug, [FromBody] UpdateRealmDto dto, CancellationToken cancellationToken)
	{
		var result = await _realmService.UpdateRealmAsync(slug, dto, cancellationToken);
		if (result.IsError)
			return Problem(result.Errors);

		var realm = result.Value;
		await _hubNotifier.EntityChangedAsync("realm", "updated", realm.Slug);
		return Ok(MapToDto(realm, realm.IsActive));
	}

	[HttpDelete("{slug}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteRealm(string slug, CancellationToken cancellationToken)
	{
		var result = await _realmService.DeleteRealmAsync(slug, cancellationToken);
		if (result.IsError)
			return Problem(result.Errors);

		await _hubNotifier.EntityChangedAsync("realm", "deleted", slug);
		return NoContent();
	}

	private static RealmDto MapToDto(Domain.Entities.Realm realm, bool needsSetup = false) => new()
	{
		Id = realm.Id,
		Slug = realm.Slug,
		DisplayName = realm.DisplayName,
		Description = realm.Description,
		Domains = realm.Domains,
		CanManageTenants = realm.CanManageTenants,
		IsActive = realm.IsActive,
		NeedsSetup = needsSetup,
		CreatedAt = realm.CreatedAt
	};
}
