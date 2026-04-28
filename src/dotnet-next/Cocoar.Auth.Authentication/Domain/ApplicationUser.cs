namespace Cocoar.Auth.Authentication.Domain;

/// <summary>
/// Identity user for ASP.NET Core Identity, backed by Marten event sourcing.
/// Profile data lives in the event stream; security data in UserSecurityData.
/// Authorization shape (principal directory) lives in a separate Cocoar.Auth.Authorization
/// Principal document (<c>Person</c>) — this type is authentication-only.
/// </summary>
public class ApplicationUser
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string NormalizedUserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public bool LockoutEnabled { get; set; } = true;
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }

    // PasswordHash is NOT stored here — it lives in UserSecurityData
    // These properties are only used transiently by Identity infrastructure
    public string? PasswordHash { get; set; }
    public string? SecurityStamp { get; set; }
    public string? ConcurrencyStamp { get; set; }

    // MFA / TOTP (persisted in UserSecurityData, transient here)
    public bool TwoFactorEnabled { get; set; }
    public string? AuthenticatorKey { get; set; }

    // Email OTP (per-user flag, persisted on ApplicationUser)
    public bool EmailOtpEnabled { get; set; }

    public ApplicationUser() { }

    public ApplicationUser(string userName, string? email = null)
    {
        Id = Guid.NewGuid();
        UserName = userName;
        NormalizedUserName = userName.ToUpperInvariant();
        Email = email;
        NormalizedEmail = email?.ToUpperInvariant();
        SecurityStamp = Guid.NewGuid().ToString();
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    public string GetDisplayLabel()
    {
        var parts = new[] { Acronym, $"{Firstname ?? ""} {Lastname ?? ""}".Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" | ", parts);
    }

}
