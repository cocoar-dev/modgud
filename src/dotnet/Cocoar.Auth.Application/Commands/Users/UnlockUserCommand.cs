using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to unlock a locked-out user account (admin action).
/// </summary>
public record UnlockUserCommand(ShortGuid Id, Guid? UnlockedByUserId = null);

/// <summary>
/// Handler for UnlockUserCommand.
/// </summary>
public class UnlockUserHandler
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserRepository _userRepository;

    public UnlockUserHandler(
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository)
    {
        _userManager = userManager;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<bool>> HandleAsync(UnlockUserCommand command, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(command.Id.Guid);
        }

        // Check if user is actually locked out
        if (!user.LockoutEnd.HasValue || user.LockoutEnd <= DateTimeOffset.UtcNow)
        {
            return UserErrors.NotLockedOut(command.Id.Guid);
        }

        // Clear lockout
        user.SetLockoutEnd(null);
        user.ResetAccessFailedCount();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return UserErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }
}
