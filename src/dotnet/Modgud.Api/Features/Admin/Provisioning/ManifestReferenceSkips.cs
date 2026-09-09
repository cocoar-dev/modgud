namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Collects the manifest references an apply could not resolve on the target realm.
///
/// <para>A manifest is content, and content travels: a partial export, a hand-written file,
/// or a realm whose apps have moved on since the file was written all produce references
/// that name something the target does not have. The applier does NOT fail on those — a
/// reference that cannot be resolved is SKIPPED, and what could be resolved is applied.
/// Ten role ids where eight exist means eight roles, not an aborted apply.</para>
///
/// <para>Two rules keep "skipped" from quietly turning into "cleared":</para>
/// <list type="number">
/// <item>Each reference resolves on its own. Missing ones drop out of the list; the rest land.</item>
/// <item>If a NON-EMPTY reference list resolves to nothing at all, the field is treated as
/// ABSENT rather than as an empty list. Under v2 merge-patch semantics an empty list is an
/// instruction ("clear this"), and the manifest never asked for that — zero resolved
/// references are a failed lookup, not an expressed intent. On a create absent and empty
/// coincide (there is nothing to keep); on an update the stored value survives, so an
/// existing role cannot be silently stripped of its permissions because its app was left
/// out of the file.</item>
/// </list>
///
/// <para>Everything skipped is recorded here so the apply can report it — silence is the
/// only genuinely dangerous outcome, and the plan shows the same thing beforehand.</para>
/// </summary>
public sealed class ManifestReferenceSkips
{
    private readonly List<string> _skips = [];

    public IReadOnlyList<string> Skips => _skips;

    /// <summary>Records one unresolvable reference. <paramref name="context"/> is the entity
    /// the reference sits on (e.g. <c>role 'acme/Author'</c>).</summary>
    public void Skip(string context, string reference, string reason)
        => _skips.Add($"{context}: {reference} — {reason}");

    /// <summary>Records that a whole reference list came back empty and is therefore left
    /// unchanged rather than cleared (rule 2 above).</summary>
    public void SkipWholeList(string context, string field, int listed)
        => _skips.Add(
            $"{context}: none of the {listed} {field} reference(s) exist in this realm — "
            + $"{field} is left unchanged rather than cleared.");
}
