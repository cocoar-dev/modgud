using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Permissions;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Computes the change plan a <see cref="RealmManifestApplier.UpdateRealmAsync"/> of the
/// given manifest WOULD produce — a pure dry-run built by diffing the manifest against the
/// realm's current <see cref="RealmManifestExporter"/> export. Nothing is written.
///
/// <para>The diff mirrors the applier's field-level merge semantics per section: an omitted
/// scalar / empty list is "no change" where the applier patches (APIs, scopes, clients,
/// login providers, users, positions), while fields whose canonical update is a full
/// replace (app catalog, role permissions, group members/roles) compare even when empty.
/// Secret-bearing fields (user password, client/provider secret, captcha secret) never
/// appear as change values — they surface as redacted notes.</para>
///
/// <para>With prune the plan also lists delete candidates (current entities absent from the
/// manifest) and marks the ones the applier's lockout/infra protection would keep. The
/// admin-conferring checks run against the CURRENT role graph, whereas the applier re-checks
/// after its upsert — a manifest that simultaneously strips and prunes an admin path can
/// therefore differ at the margin; the apply-time guards remain authoritative.</para>
/// </summary>
public sealed class RealmManifestPlanner(
    RealmManifestExporter exporter,
    IServiceScopeFactory scopeFactory,
    IOptions<JsonOptions> jsonOptions)
{
    /// <summary>
    /// Computes the plan; with a <paramref name="baseline"/> (the export snapshot a
    /// draft was created from) it additionally classifies three-way conflicts per
    /// ADR-0005: live state that moved since the baseline surfaces as
    /// staleOverwrite / bothChanged / deletedLive / createdLive conflict entries.
    /// Without a baseline the plan is the plain draft-vs-live diff.
    ///
    /// <para><paramref name="deletions"/> (staged deletes, ADR-0005) are targeted
    /// delete candidates — prune's per-entity counterpart. A targeted delete of a
    /// PROTECTED entity is an apply error (the admin explicitly asked for something
    /// the applier will never do), an already-absent target is a no-op note, and a
    /// target that changed live since the baseline flags bothChanged (git's
    /// modify/delete conflict).</para>
    /// </summary>
    public async Task<ErrorOr<RealmPlanResult>> PlanAsync(
        RealmManifest manifest, bool prune, RealmManifest? baseline = null,
        IReadOnlyCollection<RealmDraftDeletion>? deletions = null, CancellationToken ct = default)
    {
        var deleteKeys = (deletions ?? [])
            .GroupBy(d => d.Section, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Key).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        HashSet<string>? DeletesFor(string section) => deleteKeys.GetValueOrDefault(section);

        var slug = manifest.Realm.Slug;
        var exported = await exporter.ExportRealmAsync(slug, ct);
        if (exported.IsError) return exported.Errors;
        var current = exported.Value;
        var json = jsonOptions.Value.SerializerOptions;

        var result = new RealmPlanResult { Slug = slug, Prune = prune };
        AddRealmShellWarnings(manifest, current, result.Warnings);

        // The protection checks for prune candidates (does this user/group confer
        // realm:admin?) need tenant-scoped queries — same scoping as the exporter.
        using var _ = TenantContext.Enter(slug);
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<IDocumentSession>();
        var perms = sp.GetRequiredService<IPermissionService>();

        result.Sections.Add(PlanSettings(manifest, current, baseline, json));

        result.Sections.Add(await PlanSectionAsync("apps", json, prune, DeletesFor("apps"),
            manifest.Apps, current.Apps, baseline?.Apps, a => a.Slug,
            new SectionPolicy<RealmManifestApp>
            {
                Skip = ["Slug"],
                // UpdateApp replaces the whole identity + catalog, so these apply even
                // when null/empty (an empty manifest catalog would drop every entry).
                AlwaysApplied = ["Description", "Permissions"],
                NestedPatch = ["Settings"],
                DeleteNote = "Deleting fails at apply while the app is still referenced by a kept role, API or scope.",
            }));

        result.Sections.Add(await PlanSectionAsync("apis", json, prune, DeletesFor("apis"),
            manifest.Apis, current.Apis, baseline?.Apis, a => a.Name,
            new SectionPolicy<RealmManifestApi> { Skip = ["Name"] }));

        result.Sections.Add(await PlanSectionAsync("scopes", json, prune, DeletesFor("scopes"),
            manifest.Scopes, current.Scopes, baseline?.Scopes, s => s.Name,
            new SectionPolicy<RealmManifestScope> { Skip = ["Name"] }));

        result.Sections.Add(await PlanSectionAsync("clients", json, prune, DeletesFor("clients"),
            manifest.Clients, current.Clients, baseline?.Clients, c => c.ClientId,
            new SectionPolicy<RealmManifestClient>
            {
                Skip = ["ClientId", "ClientSecret"],
                ImmutableIgnored = ["ClientType"],
                PostProcess = (desired, existing, entry) =>
                {
                    if (existing is null)
                    {
                        if (string.Equals(desired.ClientType, "confidential", StringComparison.OrdinalIgnoreCase))
                            entry.Notes.Add(desired.ClientSecret is null
                                ? "A client secret will be generated and returned once by the apply."
                                : "The provided client secret will be stored at create (value not shown).");
                    }
                    else if (!string.IsNullOrWhiteSpace(desired.ClientSecret))
                    {
                        entry.Notes.Add("ClientSecret is ignored for an existing client — rotate it via the client admin instead.");
                    }
                },
            }));

        result.Sections.Add(await PlanSectionAsync("loginProviders", json, prune, DeletesFor("loginProviders"),
            manifest.LoginProviders, current.LoginProviders, baseline?.LoginProviders, p => p.Slug,
            new SectionPolicy<RealmManifestLoginProvider>
            {
                Skip = ["Slug", "ClientSecret"],
                // Type/Flavor own the provider's URLs + config shape; the applier refuses
                // a differing value outright, so the plan flags it as an apply error.
                ImmutableFails = ["Type", "Flavor"],
                PostProcess = (desired, existing, entry) =>
                {
                    if (!string.IsNullOrWhiteSpace(desired.ClientSecret))
                        entry.Notes.Add(existing is null
                            ? "The provided client secret will be stored at create (value not shown)."
                            : "The provided client secret ROTATES the stored secret (value not shown).");
                },
            }));

        result.Sections.Add(await PlanSectionAsync("roles", json, prune, DeletesFor("roles"),
            manifest.Roles, current.Roles, baseline?.Roles, r => r.Name,
            new SectionPolicy<RealmManifestRole>
            {
                Skip = ["Name", "Key"],
                // Role update is a full payload replace — omitted permissions clear the set.
                AlwaysApplied = ["Description", "App", "Permissions"],
                Protect = r => Task.FromResult<string?>(r.IsRealmAdmin
                    ? "Realm-admin roles are never pruned (lockout protection)."
                    : null),
            }));

        result.Sections.Add(await PlanUsersAsync(manifest, current, baseline, json, prune, DeletesFor("users"), session, perms, ct));

        result.Sections.Add(await PlanSectionAsync("groups", json, prune, DeletesFor("groups"),
            manifest.Groups, current.Groups, baseline?.Groups, g => g.Name,
            new SectionPolicy<RealmManifestGroup>
            {
                Skip = ["Name"],
                // Group update is a full replace: empty member/role lists clear them, a null
                // description/script/email clears the stored value. BoundTo keeps the
                // default patch rule (null = no change).
                AlwaysApplied = ["Description", "Members", "Roles", "MembershipScript", "Email"],
                Protect = async g =>
                {
                    var doc = await session.Query<Group>()
                        .FirstOrDefaultAsync(x => !x.IsDeleted && x.Name == g.Name, ct);
                    return doc is not null &&
                           await GroupMembershipGuards.GroupConfersRealmAdminAsync(session, perms, doc, ct)
                        ? "Groups conferring realm:admin are never pruned (lockout protection)."
                        : null;
                },
            }));

        result.Sections.Add(await PlanSectionAsync("positions", json, prune, DeletesFor("positions"),
            manifest.Positions, current.Positions, baseline?.Positions,
            p => p.AccountName.Trim().ToLowerInvariant(),
            new SectionPolicy<RealmManifestPosition>
            {
                Skip = ["AccountName"],
                NestedPatch = ["TerminalPolicy"],
                DeleteNote = "Deleting a position revokes its tokens, ends its staffing sessions and revokes terminal slots that only served this position.",
                PostProcess = (desired, existing, entry) =>
                {
                    if (existing is { IsActive: true } && desired.IsActive == false)
                        entry.Notes.Add("Deactivating revokes the position's outstanding tokens and ends its running staffing sessions.");
                },
            }));

        return result;
    }

    // ── Realm shell — apply never mutates it; differing values are warnings. ─────────

    private static void AddRealmShellWarnings(RealmManifest manifest, RealmManifest current, List<string> warnings)
    {
        var ignored = new List<string>();
        if (!string.IsNullOrEmpty(manifest.Realm.DisplayName) &&
            !string.Equals(manifest.Realm.DisplayName, current.Realm.DisplayName, StringComparison.Ordinal))
            ignored.Add("DisplayName");
        if (manifest.Realm.Domains is { Length: > 0 } &&
            !manifest.Realm.Domains.ToHashSet(StringComparer.Ordinal)
                .SetEquals(current.Realm.Domains ?? []))
            ignored.Add("Domains");
        if (!string.IsNullOrEmpty(manifest.Realm.PrimaryDomain) &&
            !string.Equals(manifest.Realm.PrimaryDomain, current.Realm.PrimaryDomain, StringComparison.Ordinal))
            ignored.Add("PrimaryDomain");
        if (ignored.Count > 0)
            warnings.Add($"The realm shell is not modified by apply — differing value(s) for {string.Join(", ", ignored)} are ignored.");
    }

    // ── Settings — one pseudo-entity, nested patch diff with dotted paths. ───────────

    private static RealmPlanSection PlanSettings(
        RealmManifest manifest, RealmManifest current, RealmManifest? baseline, JsonSerializerOptions json)
    {
        var section = new RealmPlanSection { Name = "settings" };
        if (manifest.Settings is null) return section;

        var entry = new RealmPlanEntry { Key = "settings", Action = "unchanged" };
        var desired = JsonSerializer.SerializeToNode(manifest.Settings, json)!.AsObject();
        var currentNode = current.Settings is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(current.Settings, json)!.AsObject();
        var baselineNode = baseline is null
            ? null
            : baseline.Settings is null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(baseline.Settings, json)!.AsObject();

        // The captcha secret is write-only — never echo it into the plan.
        if (desired["SelfRegistration"] is JsonObject selfReg && selfReg["CaptchaSecret"] is not null)
        {
            selfReg.Remove("CaptchaSecret");
            entry.Notes.Add("SelfRegistration.CaptchaSecret will be stored (value not shown).");
        }

        NestedPatchDiff(string.Empty, desired, currentNode, baselineNode, entry.Changes, entry.Conflicts);
        if (entry.Changes.Count > 0 || entry.Notes.Count > 0 || entry.Conflicts.Count > 0)
            entry = entry with { Action = "update" };
        section.Entries.Add(entry);
        return section;
    }

    // ── Users — matched by email OR username (the applier's lookup), password note. ──

    private static async Task<RealmPlanSection> PlanUsersAsync(
        RealmManifest manifest, RealmManifest current, RealmManifest? baseline,
        JsonSerializerOptions json, bool prune, HashSet<string>? deleteKeys,
        IDocumentSession session, IPermissionService perms, CancellationToken ct)
    {
        // A staged user deletion carries the key the list row showed (username or
        // email) — match either, case-insensitively (emails always are).
        static bool IsTargeted(RealmManifestUser u, HashSet<string>? keys) =>
            keys is not null && keys.Any(k =>
                string.Equals(k, u.ResolveKey(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, u.Email, StringComparison.OrdinalIgnoreCase) ||
                (u.UserName is not null && string.Equals(k, u.UserName, StringComparison.OrdinalIgnoreCase)));

        var section = new RealmPlanSection { Name = "users" };
        var byEmail = current.Users
            .Where(u => !string.IsNullOrEmpty(u.Email))
            .ToDictionary(u => u.Email.ToUpperInvariant(), u => u, StringComparer.Ordinal);
        var byUserName = current.Users
            .Where(u => !string.IsNullOrEmpty(u.UserName))
            .ToDictionary(u => u.UserName!.ToLowerInvariant(), u => u, StringComparer.Ordinal);

        static RealmManifestUser? Match(
            RealmManifestUser u,
            IReadOnlyDictionary<string, RealmManifestUser> emails,
            IReadOnlyDictionary<string, RealmManifestUser> names)
        {
            if (emails.TryGetValue(u.Email.ToUpperInvariant(), out var byMail)) return byMail;
            if (u.UserName is not null && names.TryGetValue(u.UserName.ToLowerInvariant(), out var byName)) return byName;
            return null;
        }

        var baselineByEmail = baseline?.Users
            .Where(u => !string.IsNullOrEmpty(u.Email))
            .ToDictionary(u => u.Email.ToUpperInvariant(), u => u, StringComparer.Ordinal);
        var baselineByUserName = baseline?.Users
            .Where(u => !string.IsNullOrEmpty(u.UserName))
            .ToDictionary(u => u.UserName!.ToLowerInvariant(), u => u, StringComparer.Ordinal);

        var policy = new SectionPolicy<RealmManifestUser> { Skip = ["Key", "Password", "EmailConfirmed"] };
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var u in manifest.Users)
        {
            var existing = Match(u, byEmail, byUserName);
            if (existing is not null) matched.Add(existing.ResolveKey());
            var baselineUser = baselineByEmail is null
                ? null
                : Match(u, baselineByEmail, baselineByUserName!);

            var entry = DiffEntry(u.ResolveKey(), u, existing, baselineUser,
                conflictMode: baselineByEmail is not null, json, policy);
            if (!string.IsNullOrWhiteSpace(u.Password))
                entry.Notes.Add(existing is null
                    ? "Password will be set at create (value not shown)."
                    : "Password will be UPDATED for this existing user (value not shown).");
            else if (existing is null)
                entry.Notes.Add("Created passwordless (no password in the manifest).");
            // EmailConfirmed is non-nullable, so an omitted field arrives as false — only a
            // manifest that explicitly ASKS for confirmation (true vs stored false) gets the
            // "ignored" note; the false-vs-true case is indistinguishable from omission.
            if (existing is not null && u.EmailConfirmed && !existing.EmailConfirmed)
                entry.Notes.Add("EmailConfirmed is not changed on apply — the differing manifest value is ignored.");
            if ((entry.Notes.Count > 0 || entry.Conflicts.Count > 0) && entry.Action == "unchanged")
                entry = entry with { Action = "update" };
            section.Entries.Add(entry);
        }

        if (!prune && deleteKeys is not { Count: > 0 }) return section;

        foreach (var u in current.Users.Where(u => !matched.Contains(u.ResolveKey())))
        {
            var targeted = IsTargeted(u, deleteKeys);
            if (!prune && !targeted) continue;

            // Same lockout guard as PruneAsync: anyone currently holding realm:admin stays.
            var normalizedEmail = u.Email.ToUpperInvariant();
            var person = await session.Query<Person>()
                .FirstOrDefaultAsync(p => !p.IsDeleted && p.NormalizedEmail == normalizedEmail, ct);
            var isAdmin = person is not null && await perms.HasPermissionAsync(
                person.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct);

            var entry = new RealmPlanEntry
            {
                Key = u.ResolveKey(),
                Action = isAdmin ? targeted ? "error" : "protected" : "delete",
            };
            if (isAdmin)
            {
                entry.Notes.Add(targeted
                    ? "Users holding realm:admin are never deleted (lockout protection). Unstage this deletion to proceed."
                    : "Users holding realm:admin are never pruned (lockout protection).");
            }
            else if (targeted)
            {
                var baselineUser = baselineByEmail is null ? null : Match(u, baselineByEmail, baselineByUserName!);
                if (baselineUser is not null && !JsonEquivalent(
                        JsonSerializer.SerializeToNode(u, json),
                        JsonSerializer.SerializeToNode(baselineUser, json)))
                    entry.Conflicts.Add(new RealmPlanConflict("bothChanged", null,
                        null, JsonSerializer.SerializeToNode(u, json), null));
            }
            else if (baselineByEmail is not null && Match(u, baselineByEmail, baselineByUserName!) is null)
            {
                entry.Conflicts.Add(new RealmPlanConflict("createdLive", null, null, null, null));
            }
            section.Entries.Add(entry);
        }

        // Staged deletions whose target no longer exists live — informational no-op.
        foreach (var k in (deleteKeys ?? []).Where(k =>
                     !current.Users.Any(u => IsTargeted(u, [k])) &&
                     !manifest.Users.Any(u => IsTargeted(u, [k]))))
        {
            var entry = new RealmPlanEntry { Key = k, Action = "unchanged" };
            entry.Notes.Add("Staged for deletion, but already absent live — nothing to delete.");
            section.Entries.Add(entry);
        }
        return section;
    }

    // ── Generic section diff ─────────────────────────────────────────────────────────

    /// <summary>Per-section merge-semantics knobs for the generic differ.</summary>
    private sealed class SectionPolicy<T> where T : class
    {
        /// <summary>Never diffed (natural keys, secret fields handled by hooks).</summary>
        public HashSet<string> Skip { get; init; } = [];

        /// <summary>Compared even when null / empty — fields whose canonical update is a
        /// full replace rather than a patch.</summary>
        public HashSet<string> AlwaysApplied { get; init; } = [];

        /// <summary>Immutable via the canonical update: a differing value is silently
        /// ignored by the applier — noted, not a change.</summary>
        public HashSet<string> ImmutableIgnored { get; init; } = [];

        /// <summary>Immutable and enforced: a differing value makes the apply FAIL for
        /// this entry (compared case-insensitively, matching the applier).</summary>
        public HashSet<string> ImmutableFails { get; init; } = [];

        /// <summary>Nested option-objects with their own patch semantics (app Settings,
        /// position TerminalPolicy) — diffed recursively with dotted paths.</summary>
        public HashSet<string> NestedPatch { get; init; } = [];

        /// <summary>Extra note appended to every delete candidate of the section.</summary>
        public string? DeleteNote { get; init; }

        /// <summary>Returns a protection note when the applier would NEVER prune this
        /// current entity (lockout guard), null when it is a real delete candidate.</summary>
        public Func<T, Task<string?>>? Protect { get; init; }

        /// <summary>Post-diff hook for entity-specific notes (secrets, cascades).</summary>
        public Action<T, T?, RealmPlanEntry>? PostProcess { get; init; }
    }

    private static async Task<RealmPlanSection> PlanSectionAsync<T>(
        string name, JsonSerializerOptions json, bool prune, HashSet<string>? deleteKeys,
        List<T> desired, List<T> current, List<T>? baseline, Func<T, string> key, SectionPolicy<T> policy)
        where T : class
    {
        var section = new RealmPlanSection { Name = name };
        var currentByKey = current.ToDictionary(key, c => c, StringComparer.Ordinal);
        var baselineByKey = baseline?.ToDictionary(key, b => b, StringComparer.Ordinal);
        var desiredKeys = desired.Select(key).ToHashSet(StringComparer.Ordinal);

        foreach (var item in desired)
        {
            currentByKey.TryGetValue(key(item), out var existing);
            T? baselineItem = null;
            baselineByKey?.TryGetValue(key(item), out baselineItem);
            var entry = DiffEntry(key(item), item, existing, baselineItem,
                conflictMode: baselineByKey is not null, json, policy);
            policy.PostProcess?.Invoke(item, existing, entry);
            if ((entry.Notes.Count > 0 || entry.Conflicts.Count > 0) && entry.Action == "unchanged")
                entry = entry with { Action = "update" };
            section.Entries.Add(entry);
        }

        if (!prune && deleteKeys is not { Count: > 0 }) return section;

        foreach (var item in current.Where(c => !desiredKeys.Contains(key(c))))
        {
            // Targeted (staged) deletes are prune's per-entity counterpart: without
            // prune only they are candidates; everything else absent from the
            // manifest stays untouched.
            var targeted = deleteKeys?.Contains(key(item)) == true;
            if (!prune && !targeted) continue;

            var protection = policy.Protect is null ? null : await policy.Protect(item);
            var entry = new RealmPlanEntry
            {
                Key = key(item),
                // A protected entity the admin EXPLICITLY staged for deletion is an
                // apply error (gates the apply); a prune-swept one is merely skipped.
                Action = protection is null ? "delete" : targeted ? "error" : "protected",
            };
            if (protection is not null)
            {
                entry.Notes.Add(targeted
                    ? $"{protection} Unstage this deletion to proceed."
                    : protection);
            }
            else
            {
                if (policy.DeleteNote is not null) entry.Notes.Add(policy.DeleteNote);
                if (targeted)
                {
                    // git's modify/delete: the entity changed live since the draft's
                    // baseline — deleting would silently discard those changes.
                    if (baselineByKey?.TryGetValue(key(item), out var baseItem) == true &&
                        !JsonEquivalent(
                            JsonSerializer.SerializeToNode(item, json),
                            JsonSerializer.SerializeToNode(baseItem, json)))
                        entry.Conflicts.Add(new RealmPlanConflict("bothChanged", null,
                            null, JsonSerializer.SerializeToNode(item, json), null));
                }
                else if (baselineByKey is not null && !baselineByKey.ContainsKey(key(item)))
                {
                    // A prune candidate the baseline never contained appeared live AFTER
                    // the draft was taken — pruning it would delete something the draft
                    // author never saw. Three-way conflict, not a silent delete.
                    entry.Conflicts.Add(new RealmPlanConflict("createdLive", null, null, null, null));
                }
            }
            section.Entries.Add(entry);
        }

        // Staged deletions whose target no longer exists live: intent already
        // fulfilled — informational no-op, never blocks the apply.
        foreach (var k in (deleteKeys ?? []).Where(k =>
                     !currentByKey.ContainsKey(k) && !desiredKeys.Contains(k)))
        {
            var entry = new RealmPlanEntry { Key = k, Action = "unchanged" };
            entry.Notes.Add("Staged for deletion, but already absent live — nothing to delete.");
            section.Entries.Add(entry);
        }
        return section;
    }

    /// <summary>Diffs one manifest entity against its current counterpart (create when
    /// absent) with the section's merge semantics. In conflict mode the baseline
    /// counterpart classifies every emitted change three-way (ADR-0005).</summary>
    private static RealmPlanEntry DiffEntry<T>(
        string key, T item, T? existing, T? baselineItem, bool conflictMode,
        JsonSerializerOptions json, SectionPolicy<T> policy)
        where T : class
    {
        var desired = JsonSerializer.SerializeToNode(item, json)!.AsObject();
        var currentNode = existing is null
            ? null
            : JsonSerializer.SerializeToNode(existing, json)!.AsObject();
        var baselineNode = baselineItem is null
            ? null
            : JsonSerializer.SerializeToNode(baselineItem, json)!.AsObject();

        var entry = new RealmPlanEntry { Key = key, Action = existing is null ? "create" : "update" };
        var failed = false;

        // Entity-level three-way cases: the draft still stages an entity that was
        // deleted live, or collides with one that appeared live after the baseline.
        var createdLivePending = conflictMode && existing is not null && baselineNode is null;
        if (conflictMode && existing is null && baselineNode is not null)
            entry.Conflicts.Add(new RealmPlanConflict("deletedLive", null, null, null, null));

        foreach (var (field, value) in desired)
        {
            if (policy.Skip.Contains(field)) continue;
            var currentValue = currentNode?[field];

            if (policy.ImmutableFails.Contains(field) || policy.ImmutableIgnored.Contains(field))
            {
                if (currentNode is null)
                {
                    // Create path — immutability only bites on update; report the set value.
                    if (Carries(value)) entry.Changes.Add(new RealmPlanChange(field, null, value?.DeepClone()));
                }
                else if (value is not null && !JsonEquivalent(value, currentValue, caseInsensitive: true))
                {
                    if (policy.ImmutableFails.Contains(field))
                    {
                        failed = true;
                        entry.Notes.Add($"{field} is immutable (stored '{currentValue}', manifest '{value}') — the apply FAILS for this entry. Delete and recreate it to change {field}.");
                    }
                    else
                    {
                        entry.Notes.Add($"{field} is immutable — the differing manifest value ('{value}') is ignored on apply.");
                    }
                }
                continue;
            }

            if (!Carries(value) && !policy.AlwaysApplied.Contains(field)) continue;

            if (policy.NestedPatch.Contains(field))
            {
                if (value is not JsonObject nested) continue;
                if (currentNode is null)
                    entry.Changes.Add(new RealmPlanChange(field, null, nested.DeepClone()));
                else
                    NestedPatchDiff(field, nested, currentValue as JsonObject ?? new JsonObject(),
                        conflictMode && baselineNode is not null ? baselineNode[field] as JsonObject ?? new JsonObject() : null,
                        entry.Changes, entry.Conflicts);
                continue;
            }

            if (currentNode is null)
            {
                entry.Changes.Add(new RealmPlanChange(field, null, value?.DeepClone()));
            }
            else if (!JsonEquivalent(value, currentValue))
            {
                entry.Changes.Add(new RealmPlanChange(field, currentValue?.DeepClone(), value?.DeepClone()));
                if (conflictMode && baselineNode is not null)
                    ClassifyConflict(field, value, currentValue, baselineNode[field], entry.Conflicts);
            }
        }

        if (createdLivePending && entry.Changes.Count > 0)
            entry.Conflicts.Add(new RealmPlanConflict("createdLive", null, null, null, null));

        if (failed) return entry with { Action = "error" };
        if (existing is not null && entry.Changes.Count == 0 && entry.Conflicts.Count == 0)
            return entry with { Action = "unchanged" };
        return entry;
    }

    /// <summary>Three-way classification of one emitted change (desired ≠ live):
    /// draft untouched vs baseline → live moved underneath = staleOverwrite (an apply
    /// would silently revert it); both moved → bothChanged; live still at baseline →
    /// clean staged change (no conflict).</summary>
    private static void ClassifyConflict(
        string field, JsonNode? draftValue, JsonNode? liveValue, JsonNode? baselineValue,
        List<RealmPlanConflict> conflicts)
    {
        var draftTouched = !JsonEquivalent(draftValue, baselineValue);
        var liveMoved = !JsonEquivalent(liveValue, baselineValue);
        if (!liveMoved) return;
        conflicts.Add(new RealmPlanConflict(
            draftTouched ? "bothChanged" : "staleOverwrite",
            field,
            baselineValue?.DeepClone(),
            liveValue?.DeepClone(),
            draftValue?.DeepClone()));
    }

    /// <summary>Whether a manifest value carries a change under patch semantics:
    /// null = omitted, an empty list = "no change" (the applier never clears via empty).</summary>
    private static bool Carries(JsonNode? value)
        => value is not null && value is not JsonArray { Count: 0 };

    /// <summary>Recursive diff of an option-object with patch semantics: only non-null
    /// properties are considered; nested objects recurse with a dotted path. With a
    /// baseline (conflict mode) every emitted change is classified three-way.</summary>
    private static void NestedPatchDiff(
        string path, JsonObject desired, JsonObject current, JsonObject? baseline,
        List<RealmPlanChange> changes, List<RealmPlanConflict> conflicts)
    {
        foreach (var (name, value) in desired)
        {
            if (value is null) continue;
            var field = path.Length == 0 ? name : $"{path}.{name}";
            var currentValue = current[name];
            if (value is JsonObject nested)
            {
                NestedPatchDiff(field, nested, currentValue as JsonObject ?? new JsonObject(),
                    baseline is null ? null : baseline[name] as JsonObject ?? new JsonObject(),
                    changes, conflicts);
            }
            else if (!JsonEquivalent(value, currentValue))
            {
                changes.Add(new RealmPlanChange(field, currentValue?.DeepClone(), value.DeepClone()));
                if (baseline is not null)
                    ClassifyConflict(field, value, currentValue, baseline[name], conflicts);
            }
        }
    }

    // ── Canonical JSON comparison — null == missing, arrays are order-insensitive
    //    multisets, object properties with null values are treated as absent. ──────────

    private static bool JsonEquivalent(JsonNode? a, JsonNode? b, bool caseInsensitive = false)
    {
        var ca = Canonicalize(a);
        var cb = Canonicalize(b);
        return caseInsensitive
            ? string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase)
            : string.Equals(ca, cb, StringComparison.Ordinal);
    }

    private static string Canonicalize(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o
            .Where(p => p.Value is not null)
            .Select(p => $"\"{p.Key}\":{Canonicalize(p.Value)}")
            .OrderBy(s => s, StringComparer.Ordinal)) + "}",
        JsonArray a => "[" + string.Join(",", a
            .Select(Canonicalize)
            .OrderBy(s => s, StringComparer.Ordinal)) + "]",
        _ => node.ToJsonString(),
    };
}
