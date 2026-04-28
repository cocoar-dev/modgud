using System.Text.Json;

namespace TimeToDo.Authentication.Domain.ExternalAuth.Events;

// ── External Identity Link events ────────────────────────────────────

/// <summary>
/// A user is linked (first time) to an external identity. One event per link
/// — same user + different IdP = separate link + separate stream.
/// </summary>
public record ExternalIdentityLinkedEvent(
    Guid Id,
    Guid UserId,
    Guid IdpConfigId,
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    DateTimeOffset LinkedAt);

/// <summary>
/// Recorded on every successful login via this link: a snapshot of the raw
/// IdP claims (if <c>StoreRawClaims</c> is on) plus the user-update script's
/// output (or the error message if the script threw). Pure debugging
/// artifact — the authoritative user properties live on the TimeToDo user
/// record, not here.
/// </summary>
public record ExternalIdentityScriptRecordedEvent(
    Guid Id,
    DateTimeOffset CapturedAt,
    bool ScriptSucceeded,
    JsonDocument? ScriptOutput,
    string? ScriptError,
    JsonDocument? RawClaims,
    string? Email,
    string? DisplayName);

public record ExternalIdentityUnlinkedEvent(
    Guid Id,
    DateTimeOffset UnlinkedAt,
    Guid? UnlinkedByUserId);
