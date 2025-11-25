using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to reset a user's password (admin action).
/// </summary>
public record ResetUserPasswordCommand(ShortGuid Id, string NewPassword);

/// <summary>
/// Handler for ResetUserPasswordCommand.
/// </summary>
public class ResetUserPasswordHandler
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserRepository _userRepository;

    public ResetUserPasswordHandler(
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository)
    {
        _userManager = userManager;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<bool>> HandleAsync(ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(command.Id.Guid);
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, command.NewPassword);

        if (!result.Succeeded)
        {
            return UserErrors.PasswordChangeFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }
}
