using System.Text;
using System.Text.Encodings.Web;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Service for managing two-factor authentication.
/// </summary>
public class TwoFactorService : ITwoFactorService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly UrlEncoder _urlEncoder;

    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";
    private const string Issuer = "CocoarAuth";
    private const int RecoveryCodeCount = 10;

    public TwoFactorService(UserManager<ApplicationUser> userManager, UrlEncoder urlEncoder)
    {
        _userManager = userManager;
        _urlEncoder = urlEncoder;
    }

    public async Task<ErrorOr<TwoFactorStatusDto>> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        var isEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        var hasAuthenticator = !string.IsNullOrEmpty(await _userManager.GetAuthenticatorKeyAsync(user));
        var recoveryCodesCount = await _userManager.CountRecoveryCodesAsync(user);

        return new TwoFactorStatusDto
        {
            IsEnabled = isEnabled,
            HasAuthenticator = hasAuthenticator,
            RecoveryCodesRemaining = recoveryCodesCount
        };
    }

    public async Task<ErrorOr<TwoFactorSetupDto>> GenerateSetupAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Reset the authenticator key to generate a new one
        await _userManager.ResetAuthenticatorKeyAsync(user);
        var key = await _userManager.GetAuthenticatorKeyAsync(user);

        if (string.IsNullOrEmpty(key))
        {
            return TwoFactorErrors.FailedToGenerateKey;
        }

        var authenticatorUri = GenerateAuthenticatorUri(user.Email ?? user.UserName!, key);

        return new TwoFactorSetupDto
        {
            SharedKey = FormatKey(key),
            AuthenticatorUri = authenticatorUri
        };
    }

    public async Task<ErrorOr<bool>> EnableAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Verify the code before enabling
        var isCodeValid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            code);

        if (!isCodeValid)
        {
            return TwoFactorErrors.InvalidVerificationCode;
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);

        // Generate recovery codes
        await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        return true;
    }

    public async Task<ErrorOr<bool>> DisableAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Verify the code before disabling
        var isCodeValid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            code);

        if (!isCodeValid)
        {
            return TwoFactorErrors.InvalidVerificationCode;
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);

        return true;
    }

    public async Task<ErrorOr<RecoveryCodesDto>> GenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        var isTwoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        if (!isTwoFactorEnabled)
        {
            return TwoFactorErrors.TwoFactorNotEnabled;
        }

        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        if (codes is null)
        {
            return TwoFactorErrors.FailedToGenerateCodes;
        }

        return new RecoveryCodesDto
        {
            Codes = codes.ToList()
        };
    }

    public async Task<ErrorOr<bool>> ValidateRecoveryCodeAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        var result = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, code);
        if (!result.Succeeded)
        {
            return TwoFactorErrors.InvalidRecoveryCode;
        }

        return true;
    }

    private string GenerateAuthenticatorUri(string email, string key)
    {
        return string.Format(
            AuthenticatorUriFormat,
            _urlEncoder.Encode(Issuer),
            _urlEncoder.Encode(email),
            key);
    }

    private static string FormatKey(string key)
    {
        // Format the key with spaces every 4 characters for easier reading
        var sb = new StringBuilder();
        var currentPosition = 0;
        while (currentPosition + 4 < key.Length)
        {
            sb.Append(key.AsSpan(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }
        if (currentPosition < key.Length)
        {
            sb.Append(key.AsSpan(currentPosition));
        }

        return sb.ToString().ToLowerInvariant();
    }
}
