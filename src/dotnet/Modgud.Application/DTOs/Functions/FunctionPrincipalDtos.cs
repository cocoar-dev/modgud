using Modgud.Domain.ValueObjects;

namespace Modgud.Application.DTOs.Functions;

/// <summary>
/// Wire shape for a <see cref="Modgud.Authorization.Principals.FunctionPrincipal"/>
/// (MG-FT-01) — the business identity of a function ("gate porter for customer
/// XY") staffed by changing humans on shared terminals. Like a service account
/// it carries an account name and no email; unlike one it owns no credentials —
/// its tokens are minted through the staffing flow, and terminal use is gated by
/// <see cref="TerminalPolicy"/>.
/// </summary>
public class FunctionPrincipalDto
{
    public required string Id { get; set; }
    public required string AccountName { get; set; }
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;
    public EntityStatus Status { get; set; } = EntityStatus.Active;
    public required FunctionTerminalPolicyDto TerminalPolicy { get; set; }
}

/// <summary>
/// Wire shape of the per-function terminal policy. Lifetimes travel as whole
/// minutes (matching the other lifetime DTOs on the admin surface).
/// </summary>
public class FunctionTerminalPolicyDto
{
    public bool Enabled { get; set; }
    public int StaffingSessionLifetimeMinutes { get; set; }
    public int MaximumStaffingSessionLifetimeMinutes { get; set; }
}

public class FunctionCreateDto
{
    public string AccountName { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Optional at create — omitted means terminal use stays disabled
    /// with the default lifetimes (a function is never terminal-enabled by
    /// accident).</summary>
    public FunctionTerminalPolicyUpdateDto? TerminalPolicy { get; set; }

    /// <summary>
    /// Users to authorize in the same save (modal-contract rule 5: the entity
    /// is creatable completely — like groups on user create). All-or-nothing:
    /// one invalid user rejects the whole create; the function stream and every
    /// grant stream commit in one unit of work.
    /// </summary>
    public List<string>? GrantUserIds { get; set; }
}

public class FunctionUpdateDto
{
    public string? AccountName { get; set; }
    public string? Purpose { get; set; }
    public bool? IsActive { get; set; }
    public FunctionTerminalPolicyUpdateDto? TerminalPolicy { get; set; }
}

/// <summary>Partial policy update — null fields keep the persisted value.</summary>
public class FunctionTerminalPolicyUpdateDto
{
    public bool? Enabled { get; set; }
    public int? StaffingSessionLifetimeMinutes { get; set; }
    public int? MaximumStaffingSessionLifetimeMinutes { get; set; }
}
