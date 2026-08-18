using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Marten;
using Modgud.Api.Features.Shared;
using Modgud.Domain.Users.Events;
using Modgud.Authentication.Events;
using Modgud.Authentication.Domain.ExternalAuth.Events;

namespace Modgud.Api.Features.Groups;

/// <summary>
/// Path constants that match the prefix the library's <c>MembershipEvaluator.CollectDependencies</c>
/// emits (<c>typeof(TPrincipal).Name + "."</c>). Keeping the sender side in sync
/// with the collector side so dependency-driven skips work.
/// </summary>
internal static class PrincipalPaths
{
    private const string PersonPrefix = nameof(Modgud.Authorization.Principals.Person) + ".";
    private const string GroupPrefix = nameof(Modgud.Authorization.Principals.Group) + ".";

    // Person paths
    public const string IsActive = PersonPrefix + "IsActive";
    public const string IsDeleted = PersonPrefix + "IsDeleted";
    public const string Email = PersonPrefix + "Email";
    public const string NormalizedEmail = PersonPrefix + "NormalizedEmail";
    public const string PersonFirstname = PersonPrefix + "Firstname";
    public const string PersonLastname = PersonPrefix + "Lastname";
    public const string PersonAcronym = PersonPrefix + "Acronym";
    public const string PersonUserName = PersonPrefix + "AccountName";
    public const string PersonExternalIdentities = PersonPrefix + "ExternalIdentities";

    // Group paths (for group-as-principal scripts)
    public const string GroupEmail = GroupPrefix + "Email";
    public const string GroupName = GroupPrefix + "Name";
    public const string GroupEmailMode = GroupPrefix + "EmailMode";
}

/// <summary>
/// Reacts to UserCreatedEvent — a freshly seeded principal has never been
/// matched against any auto-group. Triggers a dependency-free recalc so the
/// person lands in every auto-group whose script accepts them. Without this
/// handler, runtime-created users (POST /api/user, SSO first-login) sit
/// outside every Auto group's MemberIds until some other event fires.
/// </summary>
public class AutoMembershipOnUserCreatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnUserCreatedHandler> logger)
    : ReferenceSyncHandler<UserCreatedEvent>(logger)
{
    protected override bool ShouldSync(UserCreatedEvent @event) => true;

    // changedPaths: null forces every auto-script to re-evaluate against the
    // new principal — there's no prior state to diff against.
    protected override Task SyncAsync(UserCreatedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
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
/// Position principals participate in the same group graph as Persons and
/// Service Accounts. Keeping their auto-membership materialized is therefore
/// required for BoundTo-derived Application scopes as well as authorization.
/// Position events are full-state events, so the safe dependency signal is
/// null (re-evaluate every auto group).
/// </summary>
public class AutoMembershipOnPositionCreatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnPositionCreatedHandler> logger)
    : ReferenceSyncHandler<PositionPrincipalCreatedEvent>(logger)
{
    protected override bool ShouldSync(PositionPrincipalCreatedEvent @event) => true;

    protected override Task SyncAsync(PositionPrincipalCreatedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}

public class AutoMembershipOnPositionUpdatedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnPositionUpdatedHandler> logger)
    : ReferenceSyncHandler<PositionPrincipalUpdatedEvent>(logger)
{
    protected override bool ShouldSync(PositionPrincipalUpdatedEvent @event) => true;

    protected override Task SyncAsync(PositionPrincipalUpdatedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}

public class AutoMembershipOnPositionDeletedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnPositionDeletedHandler> logger)
    : ReferenceSyncHandler<PositionPrincipalDeletedEvent>(logger)
{
    protected override bool ShouldSync(PositionPrincipalDeletedEvent @event) => true;

    protected override Task SyncAsync(PositionPrincipalDeletedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.Id, session, changedPaths: null);
}

/// <summary>
/// Reacts to UserExternalIdentityLinkedEvent / ...UnlinkedEvent — linking or
/// unlinking an external identity mutates <c>Person.ExternalIdentities</c>, which
/// a membership script may read (e.g. "is a member of any federated IdP", or a
/// specific-provider gate). Without these handlers the durable auto-group
/// membership goes stale until an unrelated profile event happens to fire — the
/// confirmed Phase-4 gap (link/unlink triggered no recompute today).
/// <para>
/// Scoped to the <c>Person.ExternalIdentities</c> changed-path so the dependency
/// filter can skip scripts that don't read it once that optimization is live
/// (it is inert today — deps are never collected — so this currently evaluates
/// every auto-group, which is correct and cheap at link/unlink frequency).
/// </para>
/// </summary>
public class AutoMembershipOnExternalIdentityLinkedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnExternalIdentityLinkedHandler> logger)
    : ReferenceSyncHandler<UserExternalIdentityLinkedEvent>(logger)
{
    protected override bool ShouldSync(UserExternalIdentityLinkedEvent @event) => true;

    protected override Task SyncAsync(UserExternalIdentityLinkedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.PersonExternalIdentities });
}

/// <summary>
/// Symmetric to <see cref="AutoMembershipOnExternalIdentityLinkedHandler"/> — an
/// unlink (now a hard-delete) removes a ref from <c>Person.ExternalIdentities</c>,
/// so any script keyed on it must re-evaluate.
/// </summary>
public class AutoMembershipOnExternalIdentityUnlinkedHandler(
    IAutoMembershipRecalculator recalculator,
    ILogger<AutoMembershipOnExternalIdentityUnlinkedHandler> logger)
    : ReferenceSyncHandler<UserExternalIdentityUnlinkedEvent>(logger)
{
    protected override bool ShouldSync(UserExternalIdentityUnlinkedEvent @event) => true;

    protected override Task SyncAsync(UserExternalIdentityUnlinkedEvent @event, IDocumentSession session)
        => recalculator.RecalculateForPrincipalAsync(@event.UserId, session,
            new[] { PrincipalPaths.PersonExternalIdentities });
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
