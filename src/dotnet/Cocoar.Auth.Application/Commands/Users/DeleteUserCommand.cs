using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to delete a user.
/// </summary>
public record DeleteUserCommand(ShortGuid Id);

/// <summary>
/// Handler for DeleteUserCommand.
/// </summary>
public class DeleteUserHandler
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserRepository _userRepository;

    public DeleteUserHandler(
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository)
    {
        _userManager = userManager;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<bool>> HandleAsync(DeleteUserCommand command, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(command.Id.Guid);
        }

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return UserErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }
}
