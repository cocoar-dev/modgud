using Modgud.Domain.FunctionTerminals;

namespace Modgud.Application.DTOs.Functions;

/// <summary>
/// Wire shape for a <see cref="FunctionActivationGrant"/> (MG-FT-02) — the
/// right of one user to staff one function on its shared terminals. Carries
/// the user's display data (the admin list must not force N+1 lookups) and
/// whether the user owns a passkey at all — an admin granting staffing rights
/// to a passkey-less user is setting them up to fail at the terminal.
/// </summary>
public class FunctionGrantDto
{
    public required string Id { get; set; }
    public required string FunctionId { get; set; }
    public required string UserId { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserAccountName { get; set; }
    public FunctionActivationGrantStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Whether the user owns a matching passkey. With the optional
    /// <c>?rpId=</c> list-query filter this narrows to credentials enrolled
    /// under that RP ID; without it, any credential counts.
    /// </summary>
    public bool UserHasPasskey { get; set; }
}

public class FunctionGrantIssueDto
{
    public string UserId { get; set; } = string.Empty;
}
