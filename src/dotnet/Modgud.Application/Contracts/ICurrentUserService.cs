namespace Modgud.Application.Contracts;

/// <summary>
/// Service to get the current authenticated user.
/// Infrastructure layer will provide implementation from HttpContext.
/// </summary>
public interface ICurrentUserService
{
    Guid GetCurrentUserId();
    string? GetCurrentUserName();
}
