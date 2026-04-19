using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Authorization;
using Marten;

namespace Cocoar.Auth.Api.Authorization;

/// <summary>
/// Common bag of Person-side principal paths potentially affected by user events.
/// Sent to the recalculator so auto-groups whose scripts read these fields re-evaluate.
/// </summary>
internal static class GroupPrincipalPaths
{
    public static readonly string[] All =
    [
        PrincipalPaths.Email,
        PrincipalPaths.NormalizedEmail,
        PrincipalPaths.Group,
        PrincipalPaths.GroupName,
        PrincipalPaths.GroupEmailMode,
    ];
}

// ── User events ────────────────────────────────────────────────────────────

/// <summary>
/// Reacts to UserCreated — a new principal entering the directory may match
/// existing auto-group predicates (e.g. "all users").
/// </summary>
public class AutoMembershipOnUserCreatedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserCreatedHandler> logger)
    : ReferenceSyncHandler<UserCreated>(logger)
{
    protected override bool ShouldSync(UserCreated @event) => true;

    protected override Task SyncAsync(UserCreated @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session, changedPaths: null);
}

public class AutoMembershipOnUserNameChangedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserNameChangedHandler> logger)
    : ReferenceSyncHandler<UserNameChanged>(logger)
{
    protected override bool ShouldSync(UserNameChanged @event) => true;

    protected override Task SyncAsync(UserNameChanged @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.PersonUserName, PrincipalPaths.PersonNormalizedUserName });
}

public class AutoMembershipOnUserEmailChangedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserEmailChangedHandler> logger)
    : ReferenceSyncHandler<UserEmailChanged>(logger)
{
    protected override bool ShouldSync(UserEmailChanged @event) => true;

    protected override Task SyncAsync(UserEmailChanged @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.Email, PrincipalPaths.NormalizedEmail });
}

public class AutoMembershipOnUserPhoneNumberChangedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserPhoneNumberChangedHandler> logger)
    : ReferenceSyncHandler<UserPhoneNumberChanged>(logger)
{
    protected override bool ShouldSync(UserPhoneNumberChanged @event) => true;

    protected override Task SyncAsync(UserPhoneNumberChanged @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.PersonPhoneNumber });
}

public class AutoMembershipOnUserProfileNameChangedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserProfileNameChangedHandler> logger)
    : ReferenceSyncHandler<UserProfileNameChanged>(logger)
{
    protected override bool ShouldSync(UserProfileNameChanged @event) => true;

    protected override Task SyncAsync(UserProfileNameChanged @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.PersonFirstname, PrincipalPaths.PersonLastname });
}

public class AutoMembershipOnUserActivatedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserActivatedHandler> logger)
    : ReferenceSyncHandler<UserActivated>(logger)
{
    protected override bool ShouldSync(UserActivated @event) => true;

    protected override Task SyncAsync(UserActivated @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.IsActive });
}

public class AutoMembershipOnUserDeactivatedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserDeactivatedHandler> logger)
    : ReferenceSyncHandler<UserDeactivated>(logger)
{
    protected override bool ShouldSync(UserDeactivated @event) => true;

    protected override Task SyncAsync(UserDeactivated @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.IsActive });
}

/// <summary>
/// Reacts to UserDeleted — passes null changedPaths so the recalculator re-evaluates
/// every script (deleted principals can't match under a <c>!IsDeleted</c> guard and
/// fall out naturally).
/// </summary>
public class AutoMembershipOnUserDeletedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserDeletedHandler> logger)
    : ReferenceSyncHandler<UserDeleted>(logger)
{
    protected override bool ShouldSync(UserDeleted @event) => true;

    protected override Task SyncAsync(UserDeleted @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session, changedPaths: null);
}

public class AutoMembershipOnUserRestoredHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserRestoredHandler> logger)
    : ReferenceSyncHandler<UserRestored>(logger)
{
    protected override bool ShouldSync(UserRestored @event) => true;

    protected override Task SyncAsync(UserRestored @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session, changedPaths: null);
}

// ── Authorization Group events ─────────────────────────────────────────────

/// <summary>
/// Reacts to AuthorizationGroupCreatedEvent.
///  1. If this group is itself an auto-group, compute its initial members.
///  2. Other auto-groups might script "include all groups matching X" — a new group
///     entering the directory could match, re-evaluate those scripts.
/// </summary>
public class AutoMembershipOnGroupCreatedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupCreatedHandler> logger)
    : ReferenceSyncHandler<AuthorizationGroupCreatedEvent>(logger)
{
    protected override bool ShouldSync(AuthorizationGroupCreatedEvent @event) => true;

    protected override async Task SyncAsync(AuthorizationGroupCreatedEvent @event, IDocumentSession session)
    {
        var group = await session.LoadAsync<AuthorizationGroup>(@event.Id);
        if (group is null || group.IsDeleted) return;

        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session);

        // Other auto-groups may now have a new matching principal to consider.
        await recalculator.RecalculateForPrincipalAsync(@event.Id, session, GroupPrincipalPaths.All);
    }
}

/// <summary>
/// Reacts to AuthorizationGroupUpdatedEvent.
/// Two concerns:
///  1. The group itself may be an auto-group whose members need a refresh.
///  2. *Other* auto-groups may script against this group as a Principal (e.g. scripts
///     that pull in groups by name or email). Their membership can change when this
///     group's fields change — trigger a dependency-driven recalc for them.
/// </summary>
public class AutoMembershipOnGroupUpdatedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupUpdatedHandler> logger)
    : ReferenceSyncHandler<AuthorizationGroupUpdatedEvent>(logger)
{
    protected override bool ShouldSync(AuthorizationGroupUpdatedEvent @event) => true;

    protected override async Task SyncAsync(AuthorizationGroupUpdatedEvent @event, IDocumentSession session)
    {
        var group = await session.LoadAsync<AuthorizationGroup>(@event.Id);
        if (group is null || group.IsDeleted) return;

        // (1) Self-recalc if this group is an auto-group.
        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session);

        // (2) Other auto-groups that reference this group as a principal.
        await recalculator.RecalculateForPrincipalAsync(@event.Id, session, GroupPrincipalPaths.All);
    }
}

/// <summary>
/// Reacts to AuthorizationGroupDeletedEvent — removes the group from every auto-group
/// that listed it as a nested member.
/// </summary>
public class AutoMembershipOnGroupDeletedHandler(
    AutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupDeletedHandler> logger)
    : ReferenceSyncHandler<AuthorizationGroupDeletedEvent>(logger)
{
    protected override bool ShouldSync(AuthorizationGroupDeletedEvent @event) => true;

    protected override Task SyncAsync(AuthorizationGroupDeletedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}
