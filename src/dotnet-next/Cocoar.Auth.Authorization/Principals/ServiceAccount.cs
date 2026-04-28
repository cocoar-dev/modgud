namespace Cocoar.Auth.Authorization.Principals;

/// <summary>
/// Non-human principal used for machine-to-machine access — build agents,
/// integrations, scheduled jobs. Carries an account name for audit/log
/// correlation, but no email (notifications go to a responsible human or
/// group, not to the service itself).
/// </summary>
public class ServiceAccount : Principal, IPrincipalWithAccount
{
    public override string Type => "service-account";

    public string AccountName { get; set; } = "";

    /// <summary>
    /// Optional free-text explaining what this account is for. Purely descriptive —
    /// not consumed by the library's authorization logic.
    /// </summary>
    public string? Purpose { get; set; }

    public override string DisplayName => AccountName;
}
