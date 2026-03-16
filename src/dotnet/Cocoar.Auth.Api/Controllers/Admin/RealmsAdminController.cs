using Cocoar.Auth.Api.Filters;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers.Admin;

/// <summary>
/// Realm management API. Only accessible from the system realm by Admin users.
/// </summary>
[Route("api/admin/realms")]
[Authorize(Roles = "Admin")]
[SystemRealmOnly]
public class RealmsAdminController : ApiControllerBase
{
	private readonly IRealmProvisioningService _realmService;

	public RealmsAdminController(IRealmProvisioningService realmService)
	{
		_realmService = realmService;
	}

	/// <summary>
	/// List all realms.
	/// </summary>
	[HttpGet]
	[ProducesResponseType(typeof(RealmListDto), StatusCodes.Status200OK)]
	public async Task<IActionResult> GetRealms(CancellationToken cancellationToken)
	{
		var realms = await _realmService.GetAllRealmsAsync(cancellationToken);

		var items = new List<RealmDto>();
		foreach (var realm in realms)
		{
			items.Add(new RealmDto
			{
				Id = realm.Id,
				Slug = realm.Slug,
				DisplayName = realm.DisplayName,
				Description = realm.Description,
				IsActive = realm.IsActive,
				IsSystem = realm.IsSystem,
				NeedsSetup = realm.IsActive && await _realmService.NeedsSetupAsync(realm.Slug, cancellationToken),
				CreatedAt = realm.CreatedAt
			});
		}

		return Ok(new RealmListDto
		{
			Items = items,
			TotalCount = items.Count
		});
	}

	/// <summary>
	/// Get a realm by slug.
	/// </summary>
	[HttpGet("{slug}")]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetRealm(string slug, CancellationToken cancellationToken)
	{
		var realm = await _realmService.GetRealmBySlugAsync(slug, cancellationToken);
		if (realm is null)
		{
			return NotFound();
		}

		return Ok(new RealmDto
		{
			Id = realm.Id,
			Slug = realm.Slug,
			DisplayName = realm.DisplayName,
			Description = realm.Description,
			IsActive = realm.IsActive,
			IsSystem = realm.IsSystem,
			NeedsSetup = realm.IsActive && await _realmService.NeedsSetupAsync(realm.Slug, cancellationToken),
			CreatedAt = realm.CreatedAt
		});
	}

	/// <summary>
	/// Create a new realm. Provisions a new database and seeds default data.
	/// </summary>
	[HttpPost]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> CreateRealm([FromBody] CreateRealmDto dto, CancellationToken cancellationToken)
	{
		var result = await _realmService.CreateRealmAsync(dto, cancellationToken);

		return FromErrorOr(result, realm => CreatedAtAction(
			nameof(GetRealm),
			new { slug = realm.Slug },
			new RealmDto
			{
				Id = realm.Id,
				Slug = realm.Slug,
				DisplayName = realm.DisplayName,
				Description = realm.Description,
				IsActive = realm.IsActive,
				IsSystem = realm.IsSystem,
				NeedsSetup = true,
				CreatedAt = realm.CreatedAt
			}));
	}

	/// <summary>
	/// Update realm display name, description, or active status.
	/// </summary>
	[HttpPatch("{slug}")]
	[ProducesResponseType(typeof(RealmDto), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> UpdateRealm(string slug, [FromBody] UpdateRealmDto dto, CancellationToken cancellationToken)
	{
		var result = await _realmService.UpdateRealmAsync(slug, dto, cancellationToken);

		return FromErrorOr(result, realm => Ok(new RealmDto
		{
			Id = realm.Id,
			Slug = realm.Slug,
			DisplayName = realm.DisplayName,
			Description = realm.Description,
			IsActive = realm.IsActive,
			IsSystem = realm.IsSystem,
			NeedsSetup = realm.IsActive,
			CreatedAt = realm.CreatedAt
		}));
	}

	/// <summary>
	/// Soft-delete (deactivate) a realm. Cannot delete the system realm.
	/// </summary>
	[HttpDelete("{slug}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> DeleteRealm(string slug, CancellationToken cancellationToken)
	{
		var result = await _realmService.DeleteRealmAsync(slug, cancellationToken);

		if (result.IsError)
		{
			return Problem(result.Errors);
		}

		return NoContent();
	}
}
