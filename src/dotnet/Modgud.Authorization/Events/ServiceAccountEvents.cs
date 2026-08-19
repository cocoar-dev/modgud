using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Events;

/// <summary>
/// Stream events for <see cref="ServiceAccount"/>. Existing installations can
/// contain legacy document-only service accounts; their first mutation starts
/// a stream from the persisted snapshot before recording the mutation.
/// </summary>
public sealed record ServiceAccountCreatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive);

/// <summary>Full-replace service-account update.</summary>
public sealed record ServiceAccountUpdatedEvent(
    Guid Id,
    string AccountName,
    string? Purpose,
    bool IsActive);

public sealed record ServiceAccountDeletedEvent(Guid Id);
