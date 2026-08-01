namespace Modgud.Application.DTOs.User;

public class UserCreateDto
{
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Password { get; set; }
    /// <summary>
    /// Admin opt-in to mark the Identity EmailConfirmed flag at creation —
    /// skips the magic-link verify step for internal/trusted users.
    /// </summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>
    /// Whether the account can sign in. Defaults to true; set false to stage an
    /// account that only becomes usable later (onboarding ahead of a start date).
    /// The admin UI offers the same switch on create as on edit, so a user can be
    /// created complete in one step.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Direct manual group memberships that should be part of the newly
    /// created user. The create endpoint validates all groups before writing
    /// anything and commits the user and memberships together.
    /// </summary>
    public List<string> GroupIds { get; set; } = [];

    /// <summary>
    /// Per-user 2FA grace-period override. Null uses the application default.
    /// </summary>
    public int? GracePeriodDaysOverride { get; set; }

    /// <summary>
    /// Whether this user bypasses the 2FA grace period and enforcement.
    /// </summary>
    public bool TwoFactorExempt { get; set; }
}
