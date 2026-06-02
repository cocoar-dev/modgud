using Marten.Events.Aggregation;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;

namespace Modgud.Authentication.Identity.ExternalAuth;

/// <summary>
/// Inline projection building <see cref="ExternalIdentityLink"/> from its event
/// stream. Inline because the login flow reads (Issuer, Subject) → User lookups
/// on every callback; stale reads would break JIT-user-creation decisions.
/// </summary>
public partial class ExternalIdentityLinkProjection : SingleStreamProjection<ExternalIdentityLink, Guid>
{
    public ExternalIdentityLink Create(ExternalIdentityLinkedEvent @event) => new()
    {
        Id = @event.Id,
        UserId = @event.UserId,
        LoginProviderId = @event.LoginProviderId,
        Issuer = @event.Issuer,
        Subject = @event.Subject,
        Email = @event.Email,
        DisplayName = @event.DisplayName,
        LinkedAt = @event.LinkedAt,
        LastLoginAt = @event.LinkedAt,
        IsCreator = @event.IsCreator,
        IsUnlinked = false,
    };

    public ExternalIdentityLink Apply(
        ExternalIdentityScriptRecordedEvent @event,
        ExternalIdentityLink current)
    {
        current.LastLoginAt = @event.CapturedAt;
        current.LastCapturedAt = @event.CapturedAt;
        current.LastScriptOutput = @event.ScriptOutput;
        current.LastScriptSucceeded = @event.ScriptSucceeded;
        current.LastScriptError = @event.ScriptError;
        current.LastRawClaims = @event.RawClaims;
        if (@event.Email is not null) current.Email = @event.Email;
        if (@event.DisplayName is not null) current.DisplayName = @event.DisplayName;
        return current;
    }

    /// <summary>
    /// Variant C — "unlink forgets the binding". Unlinking DELETES the projection
    /// doc rather than leaving a soft <c>IsUnlinked</c> tombstone, which (a) frees
    /// the <c>(Issuer, Subject)</c> unique slot so the same identity can be
    /// re-linked, and (b) — because the delete is driven by a terminal event, not
    /// a <c>session.Delete</c> + <c>ArchiveStream</c> — a full projection rebuild
    /// replays <c>Linked → Unlinked</c> and nets to "no doc", so the slot stays
    /// free without archiving. Keeping the stream live (un-archived) is what lets a
    /// later GDPR erase still reach + mask the PII the link events carry (Marten's
    /// data-masking does not rewrite already-archived streams).
    /// </summary>
    public bool ShouldDelete(ExternalIdentityUnlinkedEvent @event) => true;
}
