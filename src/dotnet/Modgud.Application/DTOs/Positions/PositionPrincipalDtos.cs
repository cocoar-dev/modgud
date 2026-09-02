using Modgud.Domain.Common;
using Modgud.Domain.ValueObjects;

namespace Modgud.Application.DTOs.Positions;

/// <summary>
/// Wire shape for a <see cref="Modgud.Authorization.Principals.PositionPrincipal"/>
/// (MG-FT-01) — the business identity of a position ("gate porter for customer
/// XY") staffed by changing humans on shared terminals. Like a service account
/// it carries an account name and no email; unlike one it owns no credentials —
/// its tokens are minted through the staffing flow, and terminal use is gated by
/// <see cref="TerminalPolicy"/>.
/// </summary>
public class PositionPrincipalDto
{
    public required string Id { get; set; }
    public required string AccountName { get; set; }
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;
    public EntityStatus Status { get; set; } = EntityStatus.Active;
    public required PositionTerminalPolicyDto TerminalPolicy { get; set; }

    /// <summary>Only populated by the create endpoint when terminal slots were
    /// staged with the position. This is the sole response in which a generated
    /// client secret can be returned; ordinary reads leave it null.</summary>
    public IReadOnlyList<TerminalDto>? CreatedTerminals { get; set; }
}

/// <summary>
/// Wire shape of the per-position terminal policy. Lifetimes travel as whole
/// minutes (matching the other lifetime DTOs on the admin surface).
/// </summary>
public class PositionTerminalPolicyDto
{
    public bool Enabled { get; set; }
    public IReadOnlyList<string> AllowedActivationProofs { get; set; } = [];
    public IReadOnlyList<string> AllowedDeviceBindings { get; set; } = [];
    public int StaffingSessionLifetimeMinutes { get; set; }
    public int MaximumStaffingSessionLifetimeMinutes { get; set; }
}

public class PositionCreateDto
{
    public string AccountName { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Optional at create — omitted means terminal use stays disabled
    /// with the default lifetimes (a position is never terminal-enabled by
    /// accident).</summary>
    public PositionTerminalPolicyUpdateDto? TerminalPolicy { get; set; }

    /// <summary>
    /// Users to authorize in the same save (modal-contract rule 5: the entity
    /// is creatable completely — like groups on user create). All-or-nothing:
    /// one invalid user rejects the whole create; the position stream and every
    /// grant stream commit in one unit of work.
    /// </summary>
    public List<string>? GrantUserIds { get; set; }

    /// <summary>
    /// Terminal slots to set up in the same save (modal-contract rule 5 — like
    /// the service account's initial credential). Requires
    /// <see cref="TerminalPolicy"/> to enable terminal use. All-or-nothing: each
    /// slot's OAuth client is staged into the same session as the position and
    /// grant streams, so one rejected slot leaves nothing behind. Enrollment
    /// stays a later step — that is a device ceremony, not a setting.
    /// </summary>
    public List<TerminalCreateDto>? Terminals { get; set; }
}

public class PositionUpdateDto
{
    public string? AccountName { get; set; }
    /// <summary>v2 merge-patch: absent = unchanged, explicit null (or a blank
    /// string) clears, value sets.</summary>
    public Optional<string?> Purpose { get; set; }
    public bool? IsActive { get; set; }
    public PositionTerminalPolicyUpdateDto? TerminalPolicy { get; set; }
    public bool ConfirmTerminalPolicyConsequences { get; set; }
}

/// <summary>Partial policy update — null fields keep the persisted value.</summary>
public class PositionTerminalPolicyUpdateDto
{
    public bool? Enabled { get; set; }
    public IReadOnlyList<string>? AllowedActivationProofs { get; set; }
    public IReadOnlyList<string>? AllowedDeviceBindings { get; set; }
    public int? StaffingSessionLifetimeMinutes { get; set; }
    public int? MaximumStaffingSessionLifetimeMinutes { get; set; }
}

public sealed record PositionTerminalPolicyConsequencesDto
{
    public IReadOnlyList<string> TerminalIds { get; init; } = [];
    public IReadOnlyList<string> StaffingSessionIds { get; init; } = [];
    public bool HasConsequences => TerminalIds.Count > 0 || StaffingSessionIds.Count > 0;
}
