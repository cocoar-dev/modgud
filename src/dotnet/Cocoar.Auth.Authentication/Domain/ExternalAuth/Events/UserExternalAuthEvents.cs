namespace Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;

// User-stream mirror events for external-auth linking. These live on the user's
// event stream (so PrincipalDirectoryProjection — a SingleStreamProjection keyed
// by user id — can update ExternalIdentities). The matching events on the link's
// own stream carry the full payload; these mirrors only carry the minimal ref
// that the user-side projection needs.

public record UserExternalIdentityLinkedEvent(
    Guid UserId,
    Guid LinkId,
    Guid LoginProviderId,
    string Issuer,
    DateTimeOffset LinkedAt);

public record UserExternalIdentityUnlinkedEvent(
    Guid UserId,
    Guid LinkId,
    Guid LoginProviderId,
    DateTimeOffset UnlinkedAt);
