using Marten.Events.Aggregation;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;

namespace Cocoar.Auth.Authentication.Identity.ExternalAuth;

/// <summary>
/// Inline projection building <see cref="ExternalIdentityLink"/> from its event
/// stream. Inline because the login flow reads (Issuer, Subject) → User lookups
/// on every callback; stale reads would break JIT-user-creation decisions.
/// </summary>
public class ExternalIdentityLinkProjection : SingleStreamProjection<ExternalIdentityLink, Guid>
{
    public ExternalIdentityLink Create(ExternalIdentityLinkedEvent @event) => new()
    {
        Id = @event.Id,
        UserId = @event.UserId,
        IdpConfigId = @event.IdpConfigId,
        Issuer = @event.Issuer,
        Subject = @event.Subject,
        Email = @event.Email,
        DisplayName = @event.DisplayName,
        LinkedAt = @event.LinkedAt,
        LastLoginAt = @event.LinkedAt,
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

    public ExternalIdentityLink Apply(
        ExternalIdentityUnlinkedEvent @event,
        ExternalIdentityLink current)
    {
        current.IsUnlinked = true;
        return current;
    }
}
