namespace Modgud.Authorization.Principals;

/// <summary>
/// The business identity of a FUNCTION — "gate porter for customer XY", staffed
/// by changing humans on shared terminals (MG-FT-01). A fourth principal kind
/// next to Person / Group / ServiceAccount: like a service account it carries an
/// account name and no email, but unlike one it is never a credential owner —
/// function tokens are minted through the staffing flow (a person's passkey tap
/// on an enrolled terminal), and the business actor in consuming systems is the
/// function itself (<c>sub = FunctionPrincipal.Id</c>), never the person.
/// Receives ordinary group/role/permission assignments through the shared
/// Principal machinery.
/// </summary>
public sealed class FunctionPrincipal : Principal, IPrincipalWithAccount
{
    public override string Type => "function";

    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// Optional free-text explaining what this function is for. Purely
    /// descriptive — not consumed by the library's authorization logic.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Gate + lifetimes for the shared-terminal staffing flow. Disabled by
    /// default: terminal slots can only be created and enrolled once an admin
    /// deliberately enables the function for terminal use (MG-FT-03 ff.).
    /// </summary>
    public FunctionTerminalPolicy TerminalPolicy { get; set; } = FunctionTerminalPolicy.Disabled;

    public override string DisplayName => AccountName;
}

/// <summary>
/// Per-function policy for shared-terminal staffing sessions. Lifetimes follow
/// the plan's token model: a staffing session spans a shift (16 h default) with
/// an ABSOLUTE ceiling a refresh can never push past (24 h default) — the short
/// access-token lifetime is a separate, realm/client-level concern.
/// </summary>
public sealed record FunctionTerminalPolicy
{
    public bool Enabled { get; init; }

    public TimeSpan StaffingSessionLifetime { get; init; } = TimeSpan.FromHours(16);

    public TimeSpan MaximumStaffingSessionLifetime { get; init; } = TimeSpan.FromHours(24);

    public static FunctionTerminalPolicy Disabled => new()
    {
        Enabled = false,
    };
}
