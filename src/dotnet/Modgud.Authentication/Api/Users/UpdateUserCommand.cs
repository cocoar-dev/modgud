using Modgud.Domain.Common;
using Modgud.Infrastructure.FollowUps;

namespace Modgud.Authentication.Api.Users;

/// <param name="Approval">B4-AP-01 — set when the update applies a profile change request: the
/// handler marks the request Approved in the same commit as the user change, and refuses when the
/// request is no longer the pending one the caller loaded.</param>
public record UpdateUserCommand(Guid UserId, Optional<string> Firstname, Optional<string> Lastname, Optional<string> Acronym, Optional<string> Email, Optional<string> UserName, ChangeRequestApproval? Approval = null);

/// <summary>The change request an <see cref="UpdateUserCommand"/> applies, as its caller loaded it.
/// <paramref name="FollowUps"/> (B3-EI-04) are published in the commit that approves it: the
/// requester's mail and inbox item and the dismissal of the admins' items.</summary>
public record ChangeRequestApproval(Guid RequestId, DateTimeOffset SeenUpdatedAt, Guid? ReviewedByUserId, IReadOnlyList<IFollowUpMessage>? FollowUps = null,
    IReadOnlyDictionary<string, string?>? PreviousValues = null);
