using Cocoar.Auth.Authorization.Access;

namespace Cocoar.Auth.Authorization.Principals;

/// <summary>
/// Container-type principal that also carries authorization assignments:
/// <list type="bullet">
///   <item>Membership (direct, optionally driven by a predicate script)</item>
///   <item>Role grants (what actions members may perform)</item>
///   <item>Per-resource access scripts (what rows members see)</item>
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
    IReadOnlyList<Guid> IPrincipalWithMembers.MemberIds => MemberIds;

    public List<Guid> RoleIds { get; set; } = [];
    public List<ResourceAccessScript> AccessScripts { get; set; } = [];

    public MembershipMode MembershipMode { get; set; } = MembershipMode.Manual;
    public string? MembershipScript { get; set; }
    public string? CompiledMembershipScript { get; set; }

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

    public async Task<IReadOnlyList<string>> GetEmailsAsync(
        IEmailResolutionContext context,
        CancellationToken ct = default)
    {
        // Shared with an address wins directly. Shared with an empty address falls
        // back to ExpandToMembers so an admin who forgot to configure a shared mailbox
        // still gets notifications routed somewhere instead of silently dropped.
        if (EmailMode == EmailMode.Shared && !string.IsNullOrWhiteSpace(Email))
            return [Email!];

        // ExpandToMembers (or Shared-without-Email fallback): recurse through members,
        // collect each addressable email. Cycles in the group graph short-circuit via `visited`.
        var collected = new List<string>();
        var visited = new HashSet<Guid> { Id };
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
                var nestedEmails = await nested.GetEmailsAsync(context, ct);
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
