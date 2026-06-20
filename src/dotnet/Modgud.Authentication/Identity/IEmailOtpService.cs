using ErrorOr;

namespace Modgud.Authentication.Identity;

public interface IEmailOtpService
{
    /// <summary>Issues an email-OTP code as a SECOND factor (post-password 2FA).
    /// Gated on the user's <c>EmailOtpEnabled</c> opt-in.</summary>
    Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, CancellationToken ct);

    /// <summary>ADR-0010 — issues an email-OTP code as a PRIMARY passwordless
    /// factor for native (cookieless) login. Unlike <see cref="RequestOtpAsync"/>
    /// this does NOT require <c>EmailOtpEnabled</c>, but it DOES require a
    /// confirmed, active mailbox. Anti-enumeration is the caller's
    /// responsibility (the native OTP-request endpoint collapses all outcomes
    /// into a uniform response + anti-timing).</summary>
    Task<ErrorOr<bool>> RequestNativeOtpAsync(Guid userId, CancellationToken ct);

    Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken ct);
}
