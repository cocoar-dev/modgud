using Cocoar.Auth.Domain.Principals;

namespace Cocoar.Auth.Domain.Authorization;

/// <summary>
/// Central configuration object for tenant admins.
/// A group connects: Who (members) + What (roles/permissions) + Where (access scripts/data visibility).
/// <para>
/// Implements <see cref="IPrincipal"/>, <see cref="IContainerPrincipal"/>, and
/// <see cref="IEmailAddressable"/> so groups can be treated uniformly with other
/// principals (assignee, member-of, lookup, notification target).
/// </para>
/// </summary>
public class AuthorizationGroup : IPrincipal, IContainerPrincipal, IEmailAddressable
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<Guid> MemberIds { get; set; } = [];
    public List<Guid> RoleIds { get; set; } = [];
    public List<ResourceAccessScript> AccessScripts { get; set; } = [];
    public MembershipMode MembershipMode { get; set; } = MembershipMode.Manual;
    public string? MembershipScript { get; set; }
    public string? CompiledMembershipScript { get; set; }
    /// <summary>
    /// Dotted property paths the membership script reads from
    /// <c>PrincipalDirectory</c> (e.g. <c>"Person.Firstname"</c>, <c>"Email"</c>).
    /// Used to decide whether a principal-side change (e.g. a renamed user) has
    /// any chance of affecting this group's membership — lets the recalc pipeline
    /// skip groups whose scripts don't touch the changed field. <c>null</c> means
    /// "not yet analyzed / invalidate-all"; an empty list means "script is literal,
    /// never needs principal-side recompute".
    /// </summary>
    public List<string>? MembershipScriptDependencies { get; set; }
    /// <summary>
    /// Last error message from an automatic membership recompute, or null when
    /// the most recent recompute succeeded. Only meaningful for auto groups.
    /// Set by <c>AuthorizationGroupMembershipRecomputeFailedEvent</c>; cleared
    /// by <c>AuthorizationGroupMembershipRecomputedEvent</c>.
    /// </summary>
    public string? MembershipLastError { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Optional email address for notifications. Behavior controlled by <see cref="EmailMode"/>:
    /// <list type="bullet">
    ///   <item><see cref="Principals.EmailMode.Shared"/> — send directly to <see cref="Email"/>
    ///         (a shared mailbox like "team@acme.com").</item>
    ///   <item><see cref="Principals.EmailMode.ExpandToMembers"/> — resolve members recursively
    ///         and send to each member's email.</item>
    /// </list>
    /// </summary>
    public string? Email { get; set; }
    public EmailMode EmailMode { get; set; } = EmailMode.Shared;

    // ── IPrincipal / IContainerPrincipal / IEmailAddressable ───────────
    string IPrincipal.DisplayName => Name;
    string IPrincipal.Type => PrincipalType.Group;
    bool IPrincipal.IsActive => !IsDeleted;
    IReadOnlyList<Guid> IContainerPrincipal.MemberIds => MemberIds;
    string? IEmailAddressable.Email => Email;
    EmailMode IEmailAddressable.EmailMode => EmailMode;
}

public enum MembershipMode
{
    Manual = 0,
    Auto = 1
}
