namespace Modgud.Authorization.Principals;

/// <summary>
/// Container-type principal that also carries authorization assignments:
/// <list type="bullet">
///   <item>Membership (direct, optionally driven by a predicate script)</item>
///   <item>Role grants (what actions members may perform)</item>
///   <item>Email routing (shared address or expand-to-members)</item>
/// </list>
/// <para>
/// Apps derive their own class only if they want to attach extra fields;
/// the shipped <see cref="Group"/> covers the common case out of the box.
/// </para>
/// </summary>
public class Group : Principal, IPrincipalWithMembers, IPrincipalEmailAddressable
{
    public override string Type => "group";

    public string Name { get; set; } = "";
    public string? Description { get; set; }

    public override string DisplayName => Name;

    // Backing fields because the interface exposes an IReadOnlyList while the
    // domain/projection wants a mutable list — auto-property generation on the
    // interface would conflict.
    public List<Guid> MemberIds { get; set; } = [];

    // Defensive copy on the interface accessor: external readers (UI, scripts,
    // other slices) get a snapshot, not a live reference into our backing list.
    // Without this, a caller can downcast IReadOnlyList<Guid> back to List<Guid>
    // and mutate group membership through the back door.
    IReadOnlyList<Guid> IPrincipalWithMembers.MemberIds => MemberIds.ToArray();

    public List<Guid> RoleIds { get; set; } = [];

    /// <summary>
    /// App slugs in which this group is <i>active</i>. When a permission
    /// check resolves <c>(User, App)</c>, only groups with the requested
    /// app in <see cref="BoundTo"/> contribute to the user's effective
    /// permissions in that app.
    ///
    /// <para>An empty list means the group is dormant for permission
    /// purposes (organisation-only — e.g. a distribution list). Removing
    /// an app from <see cref="BoundTo"/> deactivates the group in that app
    /// without stripping its <see cref="RoleIds"/>; re-adding the app
    /// restores its effect.</para>
    /// </summary>
    public List<string> BoundTo { get; set; } = [];

    public MembershipMode MembershipMode { get; set; } = MembershipMode.Manual;
    public string? MembershipScript { get; set; }
    public string? CompiledMembershipScript { get; set; }

    /// <summary>
    /// Federation v1 (decision G): opt-in marking this group eligible to receive
    /// <i>externally-derived</i> membership at login time, computed in-memory from
    /// the current provider's claims (never written to <see cref="MemberIds"/>).
    /// Orthogonal to <see cref="MembershipMode"/> — a group can carry durable
    /// local/auto members AND accept live-session external additions.
    /// <para>
    /// A group whose roles confer <c>realm:admin</c> can NEVER be set
    /// <see cref="ExternallyDrivable"/> (bidirectional config guard) — external
    /// claims are untrusted input. Default <c>false</c>.
    /// </para>
    /// </summary>
    public bool ExternallyDrivable { get; set; }

    /// <summary>
    /// Dotted property paths the membership script reads from the principal
    /// directory (e.g. <c>"Person.Firstname"</c>, <c>"Email"</c>). The auto-membership
    /// pipeline uses this to skip groups whose scripts don't touch a changed field.
    /// <c>null</c> = not yet analyzed / invalidate-all; empty list = script is
    /// literal and never needs a principal-side recompute.
    /// </summary>
    public List<string>? MembershipScriptDependencies { get; set; }

    /// <summary>
    /// Last error message from an automatic membership recompute, or <c>null</c>
    /// when the most recent recompute succeeded. Only meaningful for auto groups.
    /// </summary>
    public string? MembershipLastError { get; set; }

    public string? Email { get; set; }
    public EmailMode EmailMode { get; set; } = EmailMode.Shared;

    public Task<IReadOnlyList<string>> GetEmailsAsync(
        IEmailResolutionContext context,
        CancellationToken ct = default) =>
        GetEmailsAsync(context, new HashSet<Guid> { Id }, ct);

    /// <summary>
    /// Internal overload that threads a shared <paramref name="visited"/> set across
    /// nested group expansions. Without this, a top-level call seeds a fresh visited
    /// set on every nested <see cref="GetEmailsAsync"/> invocation and a cycle
    /// A → B → A walks back and forth until the stack overflows.
    /// </summary>
    internal async Task<IReadOnlyList<string>> GetEmailsAsync(
        IEmailResolutionContext context,
        HashSet<Guid> visited,
        CancellationToken ct = default)
    {
        // Make sure our own Id is in the visited set even when an outer caller
        // forgot to seed it — defensive against cycles that re-enter via this group.
        visited.Add(Id);

        // Shared with an address wins directly. Shared with an empty address falls
        // back to ExpandToMembers so an admin who forgot to configure a shared mailbox
        // still gets notifications routed somewhere instead of silently dropped.
        if (EmailMode == EmailMode.Shared && !string.IsNullOrWhiteSpace(Email))
            return [Email!];

        // ExpandToMembers (or Shared-without-Email fallback): recurse through members,
        // collect each addressable email. Cycles short-circuit via the shared `visited`.
        var collected = new List<string>();
        await ExpandAsync(MemberIds, context, visited, collected, ct);
        return collected;
    }

    private static async Task ExpandAsync(
        IEnumerable<Guid> memberIds,
        IEmailResolutionContext context,
        HashSet<Guid> visited,
        List<string> collected,
        CancellationToken ct)
    {
        foreach (var id in memberIds)
        {
            if (!visited.Add(id)) continue;

            var member = await context.LoadPrincipalAsync(id, ct);
            if (member is null || !member.IsActive || member.IsDeleted) continue;

            if (member is Group nested)
            {
                // Pass the shared visited set so cross-group cycles short-circuit.
                var nestedEmails = await nested.GetEmailsAsync(context, visited, ct);
                collected.AddRange(nestedEmails);
            }
            else if (member is IPrincipalEmailAddressable addressable)
            {
                var emails = await addressable.GetEmailsAsync(context, ct);
                collected.AddRange(emails);
            }
        }
    }

}
