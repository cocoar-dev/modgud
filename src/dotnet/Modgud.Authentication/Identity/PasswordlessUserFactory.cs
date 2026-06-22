using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Identity;

/// <summary>
/// ADR-0011 — creates a brand-new <b>passwordless</b> user from an email address
/// (native passwordless registration, Phase 5). The username is derived from the
/// email local-part and disambiguated against the unique-username index, mirroring
/// the federation JIT path (<c>ExternalLoginProcessor.CreateUserJitAsync</c>). The
/// account is created <see cref="ApplicationUser.IsActive"/> = true but
/// <see cref="ApplicationUser.EmailConfirmed"/> = false; the OTP redeem that
/// follows proves the mailbox and flips EmailConfirmed.
/// </summary>
public interface IPasswordlessUserFactory
{
    Task<ApplicationUser?> CreateAsync(string email, CancellationToken ct = default);
}

public sealed class PasswordlessUserFactory(UserManager<ApplicationUser> userManager) : IPasswordlessUserFactory
{
    public async Task<ApplicationUser?> CreateAsync(string email, CancellationToken ct = default)
    {
        var atIndex = email.IndexOf('@');
        var localPart = atIndex > 0 ? email[..atIndex] : email;
        var baseUserName = new string(localPart.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(baseUserName)) baseUserName = "user";

        var candidate = baseUserName;
        var suffix = 1;
        while (await userManager.FindByNameAsync(candidate) is not null)
        {
            suffix++;
            candidate = $"{baseUserName}{suffix}";
            if (suffix > 1000) return null; // runaway safeguard
        }

        var user = new ApplicationUser(candidate, email)
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            // EmailConfirmed stays false — the OTP redeem confirms the mailbox.
        };

        // No password → passwordless account (like the magic-link / passkey users).
        var result = await userManager.CreateAsync(user);
        return result.Succeeded ? user : null;
    }
}
