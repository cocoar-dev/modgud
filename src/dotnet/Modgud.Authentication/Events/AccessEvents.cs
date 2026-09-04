namespace Modgud.Authentication.Events;

/// <summary>What a <see cref="UserAccessEndedEvent"/> ends.</summary>
public enum AccessEndScope
{
    /// <summary>One session (browser <c>UserSession</c> or native <c>ClientSession</c>).</summary>
    Session,

    /// <summary>Every session of the user (force sign-out, deactivation, deletion, erasure).</summary>
    User,
}

/// <summary>Which kind of session a <c>sid</c> names.</summary>
public enum AccessSessionKind
{
    Browser,
    Native,
}

/// <summary>
/// ADR 0009 — the reason a session (or a user's access) ended. Carried on the event,
/// on the change-feed tombstone and in the audit row; identifiers only, no free text.
/// </summary>
public static class AccessEndReasons
{
    /// <summary>The user signed out (own logout, RP-initiated logout).</summary>
    public const string Logout = "logout";

    /// <summary>Revoked from the sessions list, by an admin, a force sign-out or refresh-token reuse.</summary>
    public const string Revoked = "revoked";

    /// <summary>Idle or absolute lifetime elapsed (retention sweep).</summary>
    public const string Expired = "expired";

    public const string UserDeactivated = "user-deactivated";
    public const string UserDeleted = "user-deleted";
}

/// <summary>One relying party that held tokens for the ended session, with the issuer
/// value its tokens carried — the logout token must repeat that exact <c>iss</c>.</summary>
public sealed record AccessEndTarget(string ClientId, string Issuer);

/// <summary>
/// ADR 0009 — a relying party received its first tokens for a session (the moment the
/// Application change feed learns the <c>sid</c> the App will see). Identifiers only.
/// Appended once per session and client; further token issuance is silent.
/// </summary>
public record UserAccessGrantedEvent(
    Guid UserId,
    Guid SessionId,
    string ClientId,
    AccessSessionKind Kind,
    DateTimeOffset At);

/// <summary>
/// ADR 0009 — the fact both logout transports read: a session (or all sessions of the
/// user) ended. Appended inside the same unit of work that deletes the session and its
/// <c>SessionGrant</c> rows, so the relying parties to notify travel on the event.
/// Identifiers only — a marker on the user's history, not a session log.
/// </summary>
public record UserAccessEndedEvent(
    Guid UserId,
    AccessEndScope Scope,
    Guid? SessionId,
    List<AccessEndTarget> Targets,
    string? InitiatingClientId,
    string Reason,
    DateTimeOffset At);
