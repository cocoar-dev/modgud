namespace Modgud.Domain.FunctionTerminals.Contracts.V1;

// MG-FT-09 (plan §17) — the versioned consumer contract. These records are
// NOTIFICATIONS for consuming systems (e.g. AlertHub): correlate session
// start/end per function/terminal, refresh presence views, invalidate caches.
// They are NEVER the security boundary — a revoked token is already invalid
// server-side in Modgud before any event arrives (§17.3), and reference
// tokens die with their authorization instantly.
//
// Versioning: the namespace IS the version (Contracts.V1). Additive change =
// new optional members here; breaking change = Contracts.V2 side by side.
//
// Deliberately absent (§17.2): ActivatedByUserId, the passkey credential,
// and any person name/email — the activating human is Modgud-internal
// security audit, never consumer data. The business actor is the FUNCTION.

/// <summary>A passkey tap opened a staffing session — the function is now
/// staffed on this terminal until it ends or hits the absolute ceiling.</summary>
public sealed record FunctionStaffingSessionStarted(
    Guid FunctionPrincipalId,
    Guid TerminalEnrollmentId,
    Guid StaffingSessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset AbsoluteExpiresAt);

/// <summary>A staffing session ended (lock, supersede, expiry, or a
/// revocation cascade). The session's tokens are already revoked when this
/// is published.</summary>
public sealed record FunctionStaffingSessionEnded(
    Guid FunctionPrincipalId,
    Guid TerminalEnrollmentId,
    Guid StaffingSessionId,
    StaffingSessionEndReason Reason,
    DateTimeOffset EndedAt);

/// <summary>A terminal slot changed status (Pending→Active at enrollment,
/// disable/reactivate, terminal Revoked).</summary>
public sealed record FunctionTerminalStatusChanged(
    Guid FunctionPrincipalId,
    Guid TerminalEnrollmentId,
    TerminalEnrollmentStatus Status,
    DateTimeOffset ChangedAt);
