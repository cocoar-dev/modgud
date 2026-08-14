namespace Modgud.Domain.PositionTerminals;

/// <summary>
/// Wire constants for the position-token model (plan §7.1). Centralised here so
/// the OAuth layer, the staffing grant (MG-FT-05), and consuming systems agree
/// on the exact strings.
/// </summary>
public static class PositionGrantTypes
{
    /// <summary>The custom grant a terminal redeems a passkey tap with
    /// (MG-FT-05). Registered as a user-flow grant: the tap authenticates a
    /// human even though the minted token's subject is the position.</summary>
    public const string StaffingSession = "urn:cocoar:params:oauth:grant-type:staffing";
}

public static class PositionTokenClaimTypes
{
    public const string PrincipalType = "principal_type";
    public const string TokenUse = "token_use";
    public const string TerminalId = "terminal_id";
    public const string StaffingSessionId = "staffing_session_id";
}

public static class PositionTokenUses
{
    public const string TerminalEnrollment = "terminal_enrollment";
    public const string StaffingSession = "staffing_session";
}

public static class PositionPrincipalTypes
{
    public const string Position = "position";
}

/// <summary>Terminal-control surface constants: the audience enrollment tokens
/// are scoped to (plan §12.2 — deliberately NOT an AlertHub/business audience)
/// and the marker scope carried alongside <c>offline_access</c>.</summary>
public static class PositionTerminalControl
{
    public const string Audience = "modgud-terminal-control";
    public const string Scope = "modgud:terminal-control";
}
