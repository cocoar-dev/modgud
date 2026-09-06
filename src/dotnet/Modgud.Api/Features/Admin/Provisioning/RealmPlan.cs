using System.Text.Json.Nodes;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// The dry-run counterpart of <see cref="RealmImportResult"/>: what an apply of the given
/// manifest WOULD do to the realm, computed by <see cref="RealmManifestPlanner"/> without
/// writing anything. Entries mirror the applier's upsert-by-natural-key semantics; with
/// prune they also list the delete candidates (and which of them are protected).
/// </summary>
public sealed record RealmPlanResult
{
    public required string Slug { get; init; }

    /// <summary>Whether the plan was computed for a prune (full-sync) apply.</summary>
    public required bool Prune { get; init; }

    /// <summary>One section per manifest collection, in apply order.</summary>
    public List<RealmPlanSection> Sections { get; init; } = [];

    /// <summary>Plan-level warnings (e.g. ignored realm-shell differences).</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>True when any entry carries unresolved three-way conflicts — the
    /// draft apply endpoint refuses to run while this is set.</summary>
    public bool HasConflicts => Sections.Any(s => s.Entries.Any(e => e.Conflicts.Count > 0));
}

public sealed record RealmPlanSection
{
    /// <summary>Manifest section name, e.g. "apps", "clients", "settings".</summary>
    public required string Name { get; init; }

    public List<RealmPlanEntry> Entries { get; init; } = [];
}

public sealed record RealmPlanEntry
{
    /// <summary>The entity's natural key (app slug, client id, role name, ...).</summary>
    public required string Key { get; init; }

    /// <summary>
    /// "create" | "update" | "unchanged" | "delete" (prune candidate) |
    /// "protected" (prune candidate the applier never deletes) |
    /// "error" (the apply would fail on this entry, e.g. an immutable-field change).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>Field-level differences the apply would write.</summary>
    public List<RealmPlanChange> Changes { get; init; } = [];

    /// <summary>Human-readable remarks (rotations, ignored fields, protections).</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>Three-way conflicts against the draft's baseline (ADR-0017): live
    /// state moved since the draft was taken. Empty when no baseline was supplied.</summary>
    public List<RealmPlanConflict> Conflicts { get; init; } = [];
}

/// <summary>
/// One three-way conflict on an entry. <c>Kind</c>:
/// "staleOverwrite" — live changed a field the draft never touched (the draft still
/// carries the baseline value); applying would silently revert the interim change.
/// "bothChanged" — draft and live both changed the field to different values.
/// "deletedLive" — the entity was deleted live while the draft still stages it.
/// "createdLive" — the entity appeared live after the baseline (update collides with
/// it, or a prune would delete something the draft author never saw).
/// Resolution happens by rewriting the draft (take live / keep mine) and re-planning.
/// </summary>
public sealed record RealmPlanConflict(
    string Kind,
    string? Field,
    JsonNode? Baseline,
    JsonNode? Live,
    JsonNode? Draft);

/// <summary>One field-level difference. Secret-bearing fields are never emitted as a
/// change — they surface as a redacted note on the entry instead.</summary>
public sealed record RealmPlanChange(string Field, JsonNode? Current, JsonNode? Desired);
