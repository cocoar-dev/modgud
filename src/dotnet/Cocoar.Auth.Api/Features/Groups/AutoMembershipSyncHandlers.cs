using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Marten;
using Cocoar.Auth.Api.Features.Shared;
using Cocoar.Auth.Domain.Users.Events;
using Cocoar.Auth.Authentication.Events;

namespace Cocoar.Auth.Api.Features.Groups;

/// <summary>
/// Path constants that match the prefix the library's <c>MembershipEvaluator.CollectDependencies</c>
/// emits (<c>typeof(TPrincipal).Name + "."</c>). Keeping the sender side in sync
/// with the collector side so dependency-driven skips work.
/// </summary>
internal static class PrincipalPaths
{
    private const string PersonPrefix = nameof(Cocoar.Auth.Authorization.Principals.Person) + ".";
    private const string GroupPrefix = nameof(Cocoar.Auth.Authorization.Principals.Group) + ".";

    // Person paths
    public const string IsActive = PersonPrefix + "IsActive";
    public const string IsDeleted = PersonPrefix + "IsDeleted";
    public const string Email = PersonPrefix + "Email";
    public const string NormalizedEmail = PersonPrefix + "NormalizedEmail";
    public const string PersonFirstname = PersonPrefix + "Firstname";
    public const string PersonLastname = PersonPrefix + "Lastname";
    public const string PersonAcronym = PersonPrefix + "Acronym";
    public const string PersonUserName = PersonPrefix + "AccountName";

    // Group paths (for group-as-principal scripts)
    public const string GroupEmail = GroupPrefix + "Email";
    public const string GroupName = GroupPrefix + "Name";
    public const string GroupEmailMode = GroupPrefix + "EmailMode";
}

/// <summary>
/// Reacts to UserUpdatedEvent (fired via UpdateUserCommand dispatched through IMessageBus)
/// to re-evaluate auto-group membership for the affected user.
/// </summary>
public class AutoMembershipOnUserUpdatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserUpdatedHandler> logger)
    : ReferenceSyncHandler<UserUpdatedEvent>(logger)
{
    // Trigger on `Optional.HasValue`, not on "value actually changed". HasValue
    // means the caller intentionally included the field in the patch — even
    // when they wrote the same value back. The alternative (load the principal,
    // compare against the new value, skip on equality) would cost an extra read
    // per update. The recalculator is cheap enough that re-running on a no-op
    // patch is preferred over the load-and-compare tax on every real change.
    protected override bool ShouldSync(UserUpdatedEvent @event)
        => @event.Firstname.HasValue || @event.Lastname.HasValue || @event.Acronym.HasValue || @event.Email.HasValue;

    protected override Task SyncAsync(UserUpdatedEvent @event, IDocumentSession session)
    {
        var paths = new List<string>(4);
        if (@event.Firstname.HasValue) paths.Add(PrincipalPaths.PersonFirstname);
        if (@event.Lastname.HasValue) paths.Add(PrincipalPaths.PersonLastname);
        if (@event.Acronym.HasValue) paths.Add(PrincipalPaths.PersonAcronym);
        if (@event.Email.HasValue) { paths.Add(PrincipalPaths.Email); paths.Add(PrincipalPaths.NormalizedEmail); }
        return recalculator.RecalculateForPrincipalAsync(@event.Id, session, paths);
    }
}

/// <summary>
/// Reacts to UserActivatedEvent — scripts reading <c>p.IsActive</c> must re-evaluate.
/// </summary>
public class AutoMembershipOnUserActivatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserActivatedHandler> logger)
    : ReferenceSyncHandler<UserActivatedEvent>(logger)
{
    protected override bool ShouldSync(UserActivatedEvent @event) => true;

    protected override Task SyncAsync(UserActivatedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.IsActive });
}

/// <summary>
/// Reacts to UserDeactivatedEvent — symmetric to Activated.
/// </summary>
public class AutoMembershipOnUserDeactivatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserDeactivatedHandler> logger)
    : ReferenceSyncHandler<UserDeactivatedEvent>(logger)
{
    protected override bool ShouldSync(UserDeactivatedEvent @event) => true;

    protected override Task SyncAsync(UserDeactivatedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.IsActive });
}

/// <summary>
/// Reacts to UserDeletedEvent — the user must be removed from every auto-group.
/// Passes null changedPaths so the recalculator re-evaluates every script (deleted
/// principals can't match under a <c>!IsDeleted</c> guard and fall out naturally).
/// </summary>
public class AutoMembershipOnUserDeletedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserDeletedHandler> logger)
    : ReferenceSyncHandler<UserDeletedEvent>(logger)
{
    protected override bool ShouldSync(UserDeletedEvent @event) => true;

    protected override Task SyncAsync(UserDeletedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}

/// <summary>
/// Paths that can change on a Group-as-principal when <c>GroupUpdatedEvent</c>
/// fires. Sent to the recalculator so auto-groups whose scripts match other groups
/// as principals can re-evaluate without us computing pre/post diffs.
/// </summary>
internal static class GroupPrincipalPaths
{
    public static readonly string[] All =
    [
        PrincipalPaths.GroupEmail,
        PrincipalPaths.GroupName,
        PrincipalPaths.GroupEmailMode,
    ];
}

/// <summary>
/// Reacts to GroupUpdatedEvent.
/// Two concerns:
///  1. The group itself may be an auto-group whose members need a refresh — recalc it.
///     (UpdateGroupCommand already does this synchronously; this handler covers paths
///      that append the event outside the command flow, and is idempotent via
///      <c>MembershipLastError</c>-dedup in the recalculator.)
///  2. *Other* auto-groups may script against this group as a Principal (e.g. scripts
///     that pull in groups by name or email). Their membership can change when this
///     group's fields change — trigger a dependency-driven recalc for them.
/// </summary>
public class AutoMembershipOnGroupUpdatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupUpdatedHandler> logger)
    : ReferenceSyncHandler<GroupUpdatedEvent>(logger)
{
    // Always sync — we might need (2) even if the group itself isn't auto.
    protected override bool ShouldSync(GroupUpdatedEvent @event) => true;

    protected override async Task SyncAsync(GroupUpdatedEvent @event, IDocumentSession session)
    {
        var group = await session.LoadAsync<Group>(@event.Id);
        if (group is null || group.IsDeleted) return;

        // (1) Self-recalc if this group is an auto-group.
        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session);

        // (2) Other auto-groups that reference this group as a principal.
        await recalculator.RecalculateForPrincipalAsync(@event.Id, session, GroupPrincipalPaths.All);
    }
}

/// <summary>
/// Reacts to GroupCreatedEvent.
///  1. If this group is itself an auto-group, compute its initial members.
///  2. Other auto-groups might script "include all groups matching X" — a new group
///     entering the directory could match, re-evaluate those scripts.
/// </summary>
public class AutoMembershipOnGroupCreatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupCreatedHandler> logger)
    : ReferenceSyncHandler<GroupCreatedEvent>(logger)
{
    protected override bool ShouldSync(GroupCreatedEvent @event) => true;

    protected override async Task SyncAsync(GroupCreatedEvent @event, IDocumentSession session)
    {
        var group = await session.LoadAsync<Group>(@event.Id);
        if (group is null || group.IsDeleted) return;

        if (group.MembershipMode == MembershipMode.Auto)
            await recalculator.RecalculateForGroupAsync(group, session);

        // Other auto-groups may now have a new matching principal to consider.
        await recalculator.RecalculateForPrincipalAsync(@event.Id, session, GroupPrincipalPaths.All);
    }
}

/// <summary>
/// Reacts to GroupDeletedEvent — removes the group from every auto-group
/// that listed it as a nested member.
/// </summary>
public class AutoMembershipOnGroupDeletedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnGroupDeletedHandler> logger)
    : ReferenceSyncHandler<GroupDeletedEvent>(logger)
{
    protected override bool ShouldSync(GroupDeletedEvent @event) => true;

    protected override Task SyncAsync(GroupDeletedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}
