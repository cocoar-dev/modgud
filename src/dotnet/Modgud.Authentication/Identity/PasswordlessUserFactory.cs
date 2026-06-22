using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Identity;

/// <summary>
/// ADR-0011 — creates a brand-new <b>passwordless</b> user from an email address
/// (native passwordless registration, Phase 5). The <b>full email address is the
/// username</b>: emails are unique per realm (and the native registration callers
/// only reach here for an email that does not yet exist), so this is collision-free
/// — unlike deriving from the email local-part, where two addresses sharing a local
/// part (<c>john@a.com</c> vs <c>john@b.com</c>) would clash and one would be
/// suffixed (<c>john2</c>). The account is created
/// <see cref="ApplicationUser.IsActive"/> = true but
/// <see cref="ApplicationUser.EmailConfirmed"/> = false; the OTP redeem that follows
/// proves the mailbox and flips EmailConfirmed.
/// </summary>
public interface IPasswordlessUserFactory
{
    Task<ApplicationUser?> CreateAsync(string email, CancellationToken ct = default);

    /// <summary>As <see cref="CreateAsync(string, CancellationToken)"/>, also
    /// persisting the supplied given/family name when the (App⊕realm)
    /// registration-field policy collects them. The caller validates any
    /// <c>Required</c> name is present before reaching here.</summary>
    Task<ApplicationUser?> CreateAsync(string email, string? firstName, string? lastName, CancellationToken ct = default);
}

public sealed class PasswordlessUserFactory(UserManager<ApplicationUser> userManager) : IPasswordlessUserFactory
{
    public Task<ApplicationUser?> CreateAsync(string email, CancellationToken ct = default)
        => CreateAsync(email, firstName: null, lastName: null, ct);

    public async Task<ApplicationUser?> CreateAsync(
        string email, string? firstName, string? lastName, CancellationToken ct = default)
    {
        // The full email IS the username. The default Identity
        // AllowedUserNameCharacters set includes '@', '.', '+' and '-', so an email
        // is a valid username; and because email is unique per realm this avoids the
        // local-part collisions of the previous "derive + numeric suffix" scheme.
        var userName = email.Trim();

        var user = new ApplicationUser(userName, email)
        {
            Id = Guid.NewGuid(),
            Firstname = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim(),
            Lastname = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim(),
            IsActive = true,
            // EmailConfirmed stays false — the OTP redeem confirms the mailbox.
        };

        // No password → passwordless account (like the magic-link / passkey users).
        // CreateAsync enforces a unique normalized username, so a rare TOCTOU race
        // against a concurrent registration of the same email fails → null, which the
        // caller treats as "no code sent" (uniform anti-enumeration response).
        var result = await userManager.CreateAsync(user);
        return result.Succeeded ? user : null;
    }
}
