using Modgud.Domain.FunctionTerminals;

namespace Modgud.Application.DTOs.Functions;

/// <summary>
/// Wire shape for a <see cref="TerminalEnrollment"/> slot (MG-FT-03). The DPoP
/// key itself never leaves the server — the list only says whether the slot is
/// enrolled.
/// </summary>
public class TerminalDto
{
    public required string Id { get; set; }
    public required string FunctionId { get; set; }
    public required string DisplayName { get; set; }
    public string? Location { get; set; }
    public required string ClientId { get; set; }
    public required string WebAuthnRpId { get; set; }
    public TerminalEnrollmentStatus Status { get; set; }
    public bool Enrolled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class TerminalCreateDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Location { get; set; }

    /// <summary>The RP ID staff passkeys verify against on this terminal —
    /// typically shared across every terminal client of the consuming app
    /// (spike 3: the credential is RP-ID-scoped, not client-scoped).</summary>
    public string WebAuthnRpId { get; set; } = string.Empty;
}

public class TerminalUpdateDto
{
    public string? DisplayName { get; set; }
    public string? Location { get; set; }
}
