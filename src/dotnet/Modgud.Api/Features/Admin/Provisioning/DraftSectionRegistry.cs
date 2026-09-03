using System.Text.Json.Nodes;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Server-side map from a plan-section name to its manifest collection and the
/// natural-key rule — the authoritative twin of the frontend's SECTION_META and
/// the same key semantics the applier/planner use. Used by the staging seam
/// (<see cref="RealmDraftService.StageEntityAsync"/>) so a client only ever sends
/// a section + entity JSON and never computes keys itself.
/// </summary>
public static class DraftSectionRegistry
{
    public sealed record SectionMeta(string? Collection, Func<JsonObject, string?> Key);

    private static string? Str(JsonObject o, string prop)
        => o[prop] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static readonly Dictionary<string, SectionMeta> Sections = new(StringComparer.Ordinal)
    {
        ["settings"] = new(null, _ => "settings"),
        ["apps"] = new("Apps", o => Str(o, "Slug")),
        ["apis"] = new("Apis", o => Str(o, "Name")),
        ["scopes"] = new("Scopes", o => Str(o, "Name")),
        ["clients"] = new("Clients", o => Str(o, "ClientId")),
        ["loginProviders"] = new("LoginProviders", o => Str(o, "Slug")),
        ["roles"] = new("Roles", o => Str(o, "Key") ?? Str(o, "Name")),
        ["users"] = new("Users", o => Str(o, "Key") ?? Str(o, "UserName") ?? Str(o, "Email")),
        ["groups"] = new("Groups", o => Str(o, "Name")),
        // Upsert-only section: staged DELETIONS for service accounts are not
        // supported (the planner emits no delete candidates; delete stays live).
        ["serviceAccounts"] = new("ServiceAccounts", o => Str(o, "AccountName")?.Trim().ToLowerInvariant()),
        ["positions"] = new("Positions", o => Str(o, "AccountName")?.Trim().ToLowerInvariant()),
    };

    public static SectionMeta? Resolve(string section)
        => Sections.TryGetValue(section, out var meta) ? meta : null;
}
