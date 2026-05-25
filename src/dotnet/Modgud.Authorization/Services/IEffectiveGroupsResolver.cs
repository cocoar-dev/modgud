using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Services;

/// <summary>
/// Admin debug surface: resolves the live effective group membership of a
/// principal — direct + inherited (manual chains) + auto-script matches —
/// independent of whether <see cref="Group.MemberIds"/> is currently
/// materialized. This is the diagnostic counterpart to
/// <see cref="IPermissionService.GetUserGroupsAsync"/>, which only reflects
/// materialized state.
/// </summary>
public interface IEffectiveGroupsResolver
{
    Task<EffectiveGroupsResult> ResolveAsync(Guid principalId, CancellationToken ct = default);
}

public sealed record EffectiveGroupsResult(
    Guid PrincipalId,
    IReadOnlyList<EffectiveGroupRow> Groups,
    IReadOnlyList<GroupDiagnostic> Diagnostics);

public sealed record EffectiveGroupRow(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<EffectiveGroupRoleRef> Roles,
    EffectiveGroupSource Source,
    IReadOnlyList<EffectiveGroupViaStep>? Via,
    bool? MaterializedMatches);

public sealed record EffectiveGroupRoleRef(Guid Id, string Name);

public sealed record EffectiveGroupViaStep(Guid Id, string Name);

public enum EffectiveGroupSource
{
    /// <summary>Principal is in the group's MemberIds and the group is Manual.</summary>
    DirectManual,

    /// <summary>Principal reaches this group through a chain of nested manual groups.</summary>
    InheritedManual,

    /// <summary>The group is Auto-mode and its predicate currently evaluates to true for this principal,
    /// regardless of what MemberIds reports.</summary>
    AutoMatched,
}

public sealed record GroupDiagnostic(
    Guid GroupId,
    string GroupName,
    GroupDiagnosticKind Kind,
    string Error);

public enum GroupDiagnosticKind
{
    /// <summary>The script ran but threw at evaluation — typically a TS field reference
    /// that no longer exists on the principal.</summary>
    EvalFailed,

    /// <summary>Compiling the script (TS → LINQ predicate) failed.</summary>
    CompileFailed,
}
