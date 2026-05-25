using ErrorOr;

namespace Modgud.Authentication.Identity;

public interface IEmailOtpService
{
    Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, CancellationToken ct);
    Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken ct);
}
