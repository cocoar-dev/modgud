using ErrorOr;

namespace TimeToDo.Authentication.Identity;

public interface IEmailOtpService
{
    Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, CancellationToken ct);
    Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken ct);
}
