using Cocoar.Auth.Application.Commands.Users;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Queries.Users;
using Cocoar.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocoar.Auth.Api.Controllers.Admin;

[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
public class UsersAdminController : ApiControllerBase
{
    private readonly IMessageBus _messageBus;

    public UsersAdminController(IMessageBus messageBus)
    {
        _messageBus = messageBus;
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
        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<UserListDto>>(
            new GetUsersPagedQuery(page, pageSize, search),
            cancellationToken);
        return FromErrorOr(result);
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

        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(
            new GetUserByIdQuery(userId),
            cancellationToken);
        return FromErrorOr(result);
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
        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(
            new CreateUserCommand(dto),
            cancellationToken);
        return FromErrorOr(result, user => CreatedAtAction(nameof(GetUser), new { id = user.Id.ToString() }, user));
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

        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(
            new UpdateUserCommand(userId, dto),
            cancellationToken);
        return FromErrorOr(result);
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

        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<bool>>(
            new DeleteUserCommand(userId),
            cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

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

        var result = await _messageBus.InvokeAsync<ErrorOr.ErrorOr<bool>>(
            new ResetUserPasswordCommand(userId, dto),
            cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }
}
