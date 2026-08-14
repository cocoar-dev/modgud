using Modgud.Domain.PositionTerminals;

namespace Modgud.Application.DTOs.Positions;

/// <summary>
/// Wire shape for a <see cref="PositionGrant"/> (MG-FT-02) — the
/// right of one user to staff one position on its shared terminals. Carries
/// the user's display data (the admin list must not force N+1 lookups) and
/// whether the user owns a passkey at all — an admin granting staffing rights
/// to a passkey-less user is setting them up to fail at the terminal.
/// </summary>
public class PositionGrantDto
{
    public required string Id { get; set; }
    public required string PositionId { get; set; }
    public required string UserId { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserAccountName { get; set; }
    public PositionGrantStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Whether the user owns a matching passkey. With the optional
    /// <c>?rpId=</c> list-query filter this narrows to credentials enrolled
    /// under that RP ID; without it, any credential counts.
    /// </summary>
    public bool UserHasPasskey { get; set; }
}

public class PositionGrantIssueDto
{
    public string UserId { get; set; } = string.Empty;
}
