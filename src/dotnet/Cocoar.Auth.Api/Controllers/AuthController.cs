using System.Security.Claims;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cocoar.Auth.Api.Controllers;

[Route("api/[controller]")]
public class AuthController : ApiControllerBase
{
    private const string SessionCookieName = "cocoar.session_id";

    private readonly AuthService _authService;
    private readonly UserService _userService;
    private readonly ITwoFactorService _twoFactorService;
    private readonly IEmailOtpService _emailOtpService;
    private readonly IWebAuthnService _webAuthnService;
    private readonly IAuthenticationService _authenticationService;
    private readonly ILoginAuditService _loginAuditService;
    private readonly ISessionService _sessionService;
    private readonly IGdprService _gdprService;

    public AuthController(
        AuthService authService,
        UserService userService,
        ITwoFactorService twoFactorService,
        IEmailOtpService emailOtpService,
        IWebAuthnService webAuthnService,
        IAuthenticationService authenticationService,
        ILoginAuditService loginAuditService,
        ISessionService sessionService,
        IGdprService gdprService)
    {
        _authService = authService;
        _userService = userService;
        _twoFactorService = twoFactorService;
        _emailOtpService = emailOtpService;
        _webAuthnService = webAuthnService;
        _authenticationService = authenticationService;
        _loginAuditService = loginAuditService;
        _sessionService = sessionService;
        _gdprService = gdprService;
    }

    /// <summary>
    /// Authenticate a user with username and password.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken cancellationToken)
    {
        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _authService.LoginAsync(dto, ipAddress, userAgent, cancellationToken);

        if (!result.IsError && result.Value.Succeeded && result.Value.UserId.HasValue)
        {
            await CreateSessionCookieAsync(result.Value.UserId.Value, cancellationToken);
        }

        return FromErrorOr(result);
    }

    private string? GetClientIpAddress()
    {
        // Check for forwarded headers (when behind a proxy/load balancer)
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // X-Forwarded-For can contain multiple IPs; the first is the client
            return forwardedFor.Split(',')[0].Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Log out the current user.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var sessionId = GetCurrentSessionId();

        if (userId.HasValue && sessionId.HasValue)
        {
            await _sessionService.RevokeSessionAsync(userId.Value, sessionId.Value, cancellationToken);
        }

        DeleteSessionCookie();
        await _authService.LogoutAsync();
        return NoContent();
    }

    /// <summary>
    /// Register a new user account.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(typeof(RegisterResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto, CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var result = await _authService.RegisterAsync(dto, baseUrl, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Confirm a user's email address.
    /// </summary>
    [HttpGet("confirm-email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail([FromQuery] string userId, [FromQuery] string token, CancellationToken cancellationToken)
    {
        var dto = new ConfirmEmailDto { UserId = userId, Token = token };
        var result = await _authService.ConfirmEmailAsync(dto, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return Ok(new { message = "Email confirmed successfully." });
    }

    /// <summary>
    /// Resend email confirmation link.
    /// </summary>
    [HttpPost("resend-confirmation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationDto dto, CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        await _authService.ResendConfirmationEmailAsync(dto, baseUrl, cancellationToken);

        // Always return success to not reveal if email exists
        return Ok(new { message = "If the email exists and is not confirmed, a confirmation link has been sent." });
    }

    /// <summary>
    /// Request a password reset link.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto, CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        await _authService.ForgotPasswordAsync(dto, baseUrl, cancellationToken);

        // Always return success to not reveal if email exists
        return Ok(new { message = "If the email exists, a password reset link has been sent." });
    }

    /// <summary>
    /// Reset password using a reset token.
    /// </summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto dto, CancellationToken cancellationToken)
    {
        var result = await _authService.ResetPasswordAsync(dto, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return Ok(new { message = "Password has been reset successfully." });
    }

    /// <summary>
    /// Get the currently authenticated user's information.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var realm = User.FindFirstValue("cocoar:realm") ?? "system";
        var result = await _authService.GetCurrentUserAsync(userId.Value, cancellationToken);
        return FromErrorOr(result, user => Ok(user with { Realm = realm }));
    }

    /// <summary>
    /// Get the current user's profile.
    /// </summary>
    [HttpGet("profile")]
    [Authorize]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _authService.GetProfileAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Update the current user's profile.
    /// </summary>
    [HttpPut("profile")]
    [Authorize]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _authService.UpdateProfileAsync(userId.Value, dto, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Change the current user's password.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _userService.ChangePasswordAsync(userId.Value, dto, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    #region Two-Factor Authentication

    /// <summary>
    /// Get the current user's 2FA status.
    /// </summary>
    [HttpGet("2fa/status")]
    [Authorize]
    [ProducesResponseType(typeof(TwoFactorStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTwoFactorStatus(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _twoFactorService.GetStatusAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Generate a new authenticator key for 2FA setup.
    /// </summary>
    [HttpPost("2fa/setup")]
    [Authorize]
    [ProducesResponseType(typeof(TwoFactorSetupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetupTwoFactor(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _twoFactorService.GenerateSetupAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Enable 2FA after verifying the authenticator code.
    /// </summary>
    [HttpPost("2fa/enable")]
    [Authorize]
    [ProducesResponseType(typeof(RecoveryCodesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> EnableTwoFactor([FromBody] EnableTwoFactorDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _twoFactorService.EnableAsync(userId.Value, dto.Code, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        // Return the recovery codes after enabling
        var codesResult = await _twoFactorService.GenerateRecoveryCodesAsync(userId.Value, cancellationToken);
        return FromErrorOr(codesResult);
    }

    /// <summary>
    /// Disable 2FA after verifying the authenticator code.
    /// </summary>
    [HttpPost("2fa/disable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DisableTwoFactor([FromBody] DisableTwoFactorDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _twoFactorService.DisableAsync(userId.Value, dto.Code, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Generate new recovery codes (invalidates existing codes).
    /// </summary>
    [HttpPost("2fa/recovery-codes")]
    [Authorize]
    [ProducesResponseType(typeof(RecoveryCodesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateRecoveryCodes(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _twoFactorService.GenerateRecoveryCodesAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Complete login with a 2FA TOTP code.
    /// </summary>
    [HttpPost("2fa/login")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TwoFactorLogin([FromBody] TwoFactorLoginDto dto, CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _authenticationService.TwoFactorSignInAsync(
            dto.Code,
            isPersistent: false,
            dto.RememberMachine,
            cancellationToken);

        if (result.Succeeded)
        {
            await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);
            await CreateSessionCookieAsync(user.Id, cancellationToken);
            return Ok(new LoginResultDto { Succeeded = true });
        }

        if (result.IsLockedOut)
        {
            await _loginAuditService.RecordLoginFailedAsync(
                user.Id, ipAddress, userAgent, LoginFailureReason.LockedOut, cancellationToken);

            return Ok(new LoginResultDto
            {
                Succeeded = false,
                IsLockedOut = true,
                ErrorMessage = "This account has been locked out. Please try again later."
            });
        }

        await _loginAuditService.RecordLoginFailedAsync(
            user.Id, ipAddress, userAgent, LoginFailureReason.TwoFactorFailed, cancellationToken);

        return Ok(new LoginResultDto
        {
            Succeeded = false,
            ErrorMessage = "Invalid authenticator code."
        });
    }

    /// <summary>
    /// Complete login with a recovery code.
    /// </summary>
    [HttpPost("2fa/recovery-login")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecoveryCodeLogin([FromBody] RecoveryCodeLoginDto dto, CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _authenticationService.RecoveryCodeSignInAsync(dto.Code, cancellationToken);

        if (result.Succeeded)
        {
            await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);
            await CreateSessionCookieAsync(user.Id, cancellationToken);
            return Ok(new LoginResultDto { Succeeded = true });
        }

        if (result.IsLockedOut)
        {
            await _loginAuditService.RecordLoginFailedAsync(
                user.Id, ipAddress, userAgent, LoginFailureReason.LockedOut, cancellationToken);

            return Ok(new LoginResultDto
            {
                Succeeded = false,
                IsLockedOut = true,
                ErrorMessage = "This account has been locked out. Please try again later."
            });
        }

        await _loginAuditService.RecordLoginFailedAsync(
            user.Id, ipAddress, userAgent, LoginFailureReason.TwoFactorFailed, cancellationToken);

        return Ok(new LoginResultDto
        {
            Succeeded = false,
            ErrorMessage = "Invalid recovery code."
        });
    }

    #endregion

    #region Email OTP Two-Factor Authentication

    /// <summary>
    /// Get the current email OTP status.
    /// </summary>
    [HttpGet("2fa/email-otp/status")]
    [Authorize]
    [ProducesResponseType(typeof(EmailOtpStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEmailOtpStatus(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _emailOtpService.GetStatusAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Request an OTP code to be sent to the user's email.
    /// </summary>
    [HttpPost("2fa/email-otp/request")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestEmailOtp(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var ipAddress = GetClientIpAddress();
        var result = await _emailOtpService.RequestOtpAsync(userId.Value, ipAddress, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return Ok(new { message = "Verification code sent to your email." });
    }

    /// <summary>
    /// Verify an OTP code (for setup/verification purposes).
    /// </summary>
    [HttpPost("2fa/email-otp/verify")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyEmailOtp([FromBody] VerifyEmailOtpDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _emailOtpService.VerifyOtpAsync(userId.Value, dto.Code, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return Ok(new { message = "Verification successful." });
    }

    /// <summary>
    /// Request an OTP code during 2FA login flow (unauthenticated).
    /// </summary>
    [HttpPost("2fa/email-otp/login/request")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestEmailOtpForLogin(CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var ipAddress = GetClientIpAddress();
        var result = await _emailOtpService.RequestOtpAsync(user.Id, ipAddress, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return Ok(new { message = "Verification code sent to your email." });
    }

    /// <summary>
    /// Complete login with an email OTP code.
    /// </summary>
    [HttpPost("2fa/email-otp/login")]
    [EnableRateLimiting("auth-strict")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EmailOtpLogin([FromBody] EmailOtpLoginDto dto, CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        // Verify the OTP code
        var verifyResult = await _emailOtpService.VerifyOtpAsync(user.Id, dto.Code, cancellationToken);

        if (verifyResult.IsError)
        {
            await _loginAuditService.RecordLoginFailedAsync(
                user.Id, ipAddress, userAgent, LoginFailureReason.TwoFactorFailed, cancellationToken);

            var error = verifyResult.FirstError;
            return Ok(new LoginResultDto
            {
                Succeeded = false,
                ErrorMessage = error.Description
            });
        }

        // Sign in the user
        await _authenticationService.SignInAsync(user, isPersistent: false, cancellationToken);
        await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);
        await CreateSessionCookieAsync(user.Id, cancellationToken);

        return Ok(new LoginResultDto { Succeeded = true });
    }

    #endregion

    #region WebAuthn Two-Factor Authentication

    /// <summary>
    /// Get registration options for a new WebAuthn credential.
    /// </summary>
    [HttpPost("webauthn/register/options")]
    [Authorize]
    [ProducesResponseType(typeof(WebAuthnRegistrationOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWebAuthnRegistrationOptions(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _webAuthnService.GetRegistrationOptionsAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Complete WebAuthn credential registration.
    /// </summary>
    [HttpPost("webauthn/register/complete")]
    [Authorize]
    [ProducesResponseType(typeof(WebAuthnRegistrationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteWebAuthnRegistration(
        [FromBody] CompleteWebAuthnRegistrationDto dto,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _webAuthnService.CompleteRegistrationAsync(
            userId.Value,
            dto.AttestationResponse,
            dto.DeviceName,
            cancellationToken);

        return FromErrorOr(result);
    }

    /// <summary>
    /// Get authentication options for WebAuthn 2FA login.
    /// </summary>
    [HttpPost("webauthn/authenticate/options")]
    [ProducesResponseType(typeof(WebAuthnAuthenticationOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetWebAuthnAuthenticationOptions(CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var result = await _webAuthnService.GetAuthenticationOptionsAsync(user.Id, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Complete WebAuthn 2FA login.
    /// </summary>
    [HttpPost("webauthn/authenticate/complete")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteWebAuthnAuthentication(
        [FromBody] CompleteWebAuthnAuthenticationDto dto,
        CancellationToken cancellationToken)
    {
        var user = await _authenticationService.GetTwoFactorAuthenticationUserAsync(cancellationToken);
        if (user is null)
        {
            return Problem(TwoFactorErrors.NoTwoFactorUser);
        }

        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _webAuthnService.VerifyAuthenticationAsync(
            user.Id,
            dto.AssertionResponse,
            ipAddress,
            cancellationToken);

        if (result.IsError)
        {
            await _loginAuditService.RecordLoginFailedAsync(
                user.Id, ipAddress, userAgent, LoginFailureReason.TwoFactorFailed, cancellationToken);

            var error = result.FirstError;
            return Ok(new LoginResultDto
            {
                Succeeded = false,
                ErrorMessage = error.Description
            });
        }

        // Sign in the user
        await _authenticationService.SignInAsync(user, isPersistent: false, cancellationToken);
        await _loginAuditService.RecordLoginAsync(user.Id, ipAddress, userAgent, cancellationToken);
        await CreateSessionCookieAsync(user.Id, cancellationToken);

        return Ok(new LoginResultDto { Succeeded = true });
    }

    /// <summary>
    /// Get authentication options for passwordless WebAuthn login.
    /// </summary>
    [HttpPost("webauthn/login/options")]
    [ProducesResponseType(typeof(WebAuthnAuthenticationOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetWebAuthnLoginOptions(
        [FromBody] WebAuthnLoginOptionsRequestDto? dto,
        CancellationToken cancellationToken)
    {
        Guid? userId = null;

        // If username is provided, look up the user
        if (!string.IsNullOrEmpty(dto?.UserName))
        {
            var user = await _userService.GetByUserNameAsync(dto.UserName, cancellationToken);
            if (user.IsError)
            {
                // Don't reveal if user exists - just return options for discoverable credentials
                userId = null;
            }
            else
            {
                userId = Guid.Parse(user.Value.Id);
            }
        }

        var result = await _webAuthnService.GetAuthenticationOptionsAsync(userId, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Complete passwordless WebAuthn login.
    /// </summary>
    [HttpPost("webauthn/login/complete")]
    [ProducesResponseType(typeof(LoginResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteWebAuthnLogin(
        [FromBody] CompleteWebAuthnAuthenticationDto dto,
        CancellationToken cancellationToken)
    {
        var ipAddress = GetClientIpAddress();
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _webAuthnService.VerifyAuthenticationAsync(
            null,
            dto.AssertionResponse,
            ipAddress,
            cancellationToken);

        if (result.IsError)
        {
            var error = result.FirstError;
            return Ok(new LoginResultDto
            {
                Succeeded = false,
                ErrorMessage = error.Description
            });
        }

        // Get the user and sign in
        var user = await _userService.GetByIdAsync(result.Value, cancellationToken);
        if (user.IsError)
        {
            return Ok(new LoginResultDto
            {
                Succeeded = false,
                ErrorMessage = "User not found."
            });
        }

        await _authenticationService.SignInByIdAsync(result.Value, isPersistent: false, cancellationToken);
        await _loginAuditService.RecordLoginAsync(result.Value, ipAddress, userAgent, cancellationToken);
        await CreateSessionCookieAsync(result.Value, cancellationToken);

        return Ok(new LoginResultDto { Succeeded = true });
    }

    /// <summary>
    /// Get all WebAuthn credentials for the current user.
    /// </summary>
    [HttpGet("webauthn/credentials")]
    [Authorize]
    [ProducesResponseType(typeof(WebAuthnCredentialListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWebAuthnCredentials(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _webAuthnService.GetCredentialsAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Delete a WebAuthn credential.
    /// </summary>
    [HttpDelete("webauthn/credentials/{credentialId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWebAuthnCredential(string credentialId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _webAuthnService.DeleteCredentialAsync(userId.Value, credentialId, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Rename a WebAuthn credential.
    /// </summary>
    [HttpPatch("webauthn/credentials/{credentialId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenameWebAuthnCredential(
        string credentialId,
        [FromBody] RenameWebAuthnCredentialDto dto,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _webAuthnService.RenameCredentialAsync(userId.Value, credentialId, dto.Name, cancellationToken);

        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    #endregion

    #region Session Management

    /// <summary>
    /// Get all active sessions for the current user.
    /// </summary>
    [HttpGet("sessions")]
    [Authorize]
    [ProducesResponseType(typeof(SessionListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSessions(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var currentSessionId = GetCurrentSessionId();
        var result = await _sessionService.GetSessionsAsync(userId.Value, currentSessionId, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Revoke a specific session.
    /// </summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _sessionService.RevokeSessionAsync(userId.Value, sessionId, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Revoke all sessions except the current one (logout everywhere else).
    /// </summary>
    [HttpDelete("sessions")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var currentSessionId = GetCurrentSessionId();
        var result = await _sessionService.RevokeAllSessionsAsync(userId.Value, currentSessionId, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    private Guid? GetCurrentSessionId()
    {
        if (Request.Cookies.TryGetValue(SessionCookieName, out var value)
            && Guid.TryParse(value, out var id))
            return id;
        return null;
    }

    #endregion

    #region GDPR / Data Protection

    /// <summary>
    /// Export all user data (GDPR Article 20 - Right to Data Portability).
    /// </summary>
    [HttpGet("export-data")]
    [Authorize]
    [ProducesResponseType(typeof(UserDataExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExportData(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.ExportUserDataAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Request account deletion (initiates confirmation period).
    /// </summary>
    [HttpPost("delete-account")]
    [Authorize]
    [ProducesResponseType(typeof(DeletionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestDeletion([FromBody] RequestDeletionDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.RequestDeletionAsync(userId.Value, dto.Password, dto.Reason, cancellationToken);
        return FromErrorOr(result);
    }

    /// <summary>
    /// Confirm account deletion with token from email.
    /// </summary>
    [HttpPost("confirm-deletion")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmDeletion([FromBody] ConfirmDeletionDto dto, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.ConfirmDeletionAsync(userId.Value, dto.Token, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        // Sign out the user after deletion
        await _authService.LogoutAsync();
        return NoContent();
    }

    /// <summary>
    /// Cancel a pending deletion request.
    /// </summary>
    [HttpPost("cancel-deletion")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelDeletion(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.CancelDeletionAsync(userId.Value, cancellationToken);
        if (result.IsError)
        {
            return Problem(result.Errors);
        }

        return NoContent();
    }

    /// <summary>
    /// Get the current deletion status.
    /// </summary>
    [HttpGet("deletion-status")]
    [Authorize]
    [ProducesResponseType(typeof(DeletionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDeletionStatus(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _gdprService.GetDeletionStatusAsync(userId.Value, cancellationToken);
        return FromErrorOr(result);
    }

    #endregion

    private async Task CreateSessionCookieAsync(Guid userId, CancellationToken ct)
    {
        var ip = GetClientIpAddress();
        var ua = Request.Headers.UserAgent.ToString();
        var result = await _sessionService.CreateSessionAsync(userId, ip, ua, ct);
        if (!result.IsError)
        {
            Response.Cookies.Append(SessionCookieName, result.Value.Id.ToString(), new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true
            });
        }
    }

    private void DeleteSessionCookie() => Response.Cookies.Delete(SessionCookieName);

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
