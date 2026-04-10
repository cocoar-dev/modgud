using System.Security.Claims;
using Cocoar.Auth.Api.Extensions;
using Cocoar.Auth.Application.Commands.Users;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Application.Queries.Users;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cocoar.Auth.Api.Hubs;
using Wolverine;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
public class UsersAdminController : ApiControllerBase
{
    private readonly IMessageBus _messageBus;
    private readonly ISessionService _sessionService;
    private readonly IGdprService _gdprService;
    private readonly IAdminHubNotifier _hubNotifier;

    public UsersAdminController(IMessageBus messageBus, ISessionService sessionService, IGdprService gdprService, IAdminHubNotifier hubNotifier)
    {
        _messageBus = messageBus;
        _sessionService = sessionService;
        _gdprService = gdprService;
        _hubNotifier = hubNotifier;
    }

    /// <summary>
    /// Get a paginated list of users.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<GetUsersPagedResult>>(
            GetTenantId(), new GetUsersPagedQuery(page, pageSize, search),
            cancellationToken);

        return result.Match(
            pagedResult => Ok(new UserListDto
            {
                Items = pagedResult.Users.Select(UserMapper.ToListDto).ToList(),
                TotalCount = pagedResult.TotalCount,
                Page = page,
                PageSize = pageSize
            }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Get a user by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<UserDetailsReadModel>>(
            GetTenantId(), new GetUserByIdQuery(userId),
            cancellationToken);

        return result.Match(
            user => Ok(UserMapper.ToDto(user)),
            errors => Problem(errors));
    }

    /// <summary>
    /// Create a new user.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto, CancellationToken cancellationToken)
    {
        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<ApplicationUser>>(
            GetTenantId(), dto.ToCommand(),
            cancellationToken);

        if (result.IsError) return Problem(result.Errors);

        var user = result.Value;
        await _hubNotifier.EntityChangedAsync("user", "created", user.Id.ToString());
        return CreatedAtAction(nameof(GetUser), new { id = user.Id.ToString() }, UserMapper.ToDto(user));
    }

    /// <summary>
    /// Update an existing user.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateUser(string id, [FromBody] UpdateUserDto dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<ApplicationUser>>(
            GetTenantId(), dto.ToCommand(userId),
            cancellationToken);

        if (result.IsError) return Problem(result.Errors);

        var user = result.Value;
        await _hubNotifier.EntityChangedAsync("user", "updated", user.Id.ToString());
        return Ok(UserMapper.ToDto(user));
    }

    /// <summary>
    /// Delete a user.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
            GetTenantId(), new DeleteUserCommand(userId),
            cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        await _hubNotifier.EntityChangedAsync("user", "deleted", id);
        return NoContent();
    }

    /// <summary>
    /// Reset a user's password (admin action).
    /// </summary>
    [HttpPost("{id}/reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordDto dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
            GetTenantId(), dto.ToCommand(userId),
            cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Unlock a locked-out user account (admin action).
    /// </summary>
    [HttpPost("{id}/unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlockUser(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        // Get the admin user ID from claims
        var adminUserId = GetCurrentUserId();

        var result = await _messageBus.InvokeForTenantAsync<ErrorOr.ErrorOr<bool>>(
            GetTenantId(), new UnlockUserCommand(userId, adminUserId),
            cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Get all active sessions for a user (admin action).
    /// </summary>
    [HttpGet("{id}/sessions")]
    [ProducesResponseType(typeof(SessionListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserSessions(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _sessionService.GetSessionsAsync(userId, currentSessionId: null, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Force logout: revoke all sessions for a user (admin action).
    /// </summary>
    [HttpDelete("{id}/sessions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAllUserSessions(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _sessionService.RevokeAllSessionsAsync(userId, exceptSessionId: null, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Soft delete a user (admin action).
    /// User can be restored later.
    /// </summary>
    [HttpPost("{id}/soft-delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SoftDeleteUser(string id, [FromBody] AdminSoftDeleteDto? dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var adminUserId = GetCurrentUserId();
        if (adminUserId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.SoftDeleteUserAsync(userId, adminUserId.Value, dto?.Reason, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Restore a soft-deleted user (admin action).
    /// </summary>
    [HttpPost("{id}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestoreUser(string id, [FromBody] AdminRestoreDto? dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var adminUserId = GetCurrentUserId();
        if (adminUserId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.RestoreUserAsync(userId, adminUserId.Value, dto?.Reason, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Permanently erase user data (GDPR - admin action).
    /// This masks all PII in the event stream and archives it. Cannot be undone.
    /// </summary>
    [HttpDelete("{id}/permanent")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PermanentlyEraseUser(string id, [FromBody] AdminPermanentEraseDto dto, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var adminUserId = GetCurrentUserId();
        if (adminUserId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.PermanentlyEraseUserDataAsync(userId, adminUserId.Value, dto.Reason, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Get a user's deletion status (admin action).
    /// </summary>
    [HttpGet("{id}/deletion-status")]
    [ProducesResponseType(typeof(DeletionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserDeletionStatus(string id, CancellationToken cancellationToken)
    {
        if (!ShortGuid.TryParse(id, out Guid userId))
        {
            return BadRequest("Invalid user ID format.");
        }

        var result = await _gdprService.GetDeletionStatusAsync(userId, cancellationToken);
        return FromErrorOr(result);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return null;
        }

        return userId;
    }
}
