using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// CRUD + plan/apply orchestration for <see cref="RealmDraft"/>s (ADR-0017 Phase 1).
///
/// <para>Secret handling: every manifest write runs through
/// <see cref="SanitizeManifest"/>, which moves secret-bearing field values (user
/// Password, client/provider ClientSecret, the captcha secret) into
/// DataProtection-encrypted slot entries and strips them from the stored manifest.
/// <see cref="MergeSecrets"/> reverses that in memory for plan/apply. Slots whose
/// target entity vanished from the manifest are dropped on the same write.</para>
///
/// <para>Visibility: a private draft belongs to its creator — other admins get a
/// NotFound, never a Forbidden (no draft-existence oracle). A shared draft is
/// readable, editable and applicable by every realm admin (the endpoint gate).</para>
/// </summary>
public sealed class RealmDraftService(
    IDocumentSession session,
    RealmManifestExporter exporter,
    RealmManifestPlanner planner,
    RealmManifestApplier applier,
    IDataProtectionProvider dataProtection,
    IOptions<JsonOptions> jsonOptions,
    TimeProvider time)
{
    private const string ProtectorPurpose = "Modgud.RealmDraft.Secrets";

    private static readonly Error NotFound =
        Error.NotFound("Draft.NotFound", "The draft does not exist (or is private to another admin).");

    // ── CRUD ─────────────────────────────────────────────────────────────────────

    public async Task<List<RealmDraftSummaryDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var drafts = await session.Query<RealmDraft>()
            .Where(d => d.Shared || d.CreatedBy == userId)
            .OrderByDescending(d => d.LastModifiedAt)
            .ToListAsync(ct);
        return drafts.Select(d => Summary(d, userId)).ToList();
    }

    public async Task<ErrorOr<RealmDraftDto>> CreateAsync(
        CreateRealmDraftDto dto, string slug, Guid userId, string userName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Error.Validation("Draft.NameRequired", "A draft needs a name.");

        var exportResult = await exporter.ExportRealmAsync(slug, ct);
        if (exportResult.IsError) return exportResult.Errors;
        var baseline = exportResult.Value;

        var manifest = dto.Source.ToLowerInvariant() switch
        {
            "export" => baseline,
            "empty" => new RealmManifest { Realm = new() { Slug = slug } },
            "manifest" when dto.Manifest is not null => dto.Manifest,
            "manifest" => null,
            _ => null,
        };
        if (manifest is null)
            return Error.Validation("Draft.InvalidSource",
                "Source must be 'export', 'empty', or 'manifest' (with a Manifest payload).");

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        manifest = SanitizeManifest(PinSlug(manifest, slug), secrets);

        var now = time.GetUtcNow();
        var draft = new RealmDraft
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Manifest = manifest,
            Baseline = baseline,
            Secrets = secrets,
            CreatedBy = userId,
            CreatedByName = userName,
            CreatedAt = now,
            LastModifiedBy = userId,
            LastModifiedByName = userName,
            LastModifiedAt = now,
            Version = 1,
        };
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    public async Task<ErrorOr<RealmDraftDto>> GetAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        return draft is null ? NotFound : ToDto(draft, userId);
    }

    public async Task<ErrorOr<RealmDraftDto>> UpdateAsync(
        Guid id, UpdateRealmDraftDto dto, string slug, Guid userId, string userName, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        if (draft.Version != dto.ExpectedVersion)
            return Error.Conflict("Draft.VersionConflict",
                $"The draft was changed by {draft.LastModifiedByName} (version {draft.Version}, you edited version {dto.ExpectedVersion}). Reload it and re-apply your change.");

        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Error.Validation("Draft.NameRequired", "A draft needs a name.");
            draft.Name = dto.Name.Trim();
        }
        if (dto.Shared.HasValue) draft.Shared = dto.Shared.Value;
        if (dto.Manifest is not null)
            draft.Manifest = SanitizeManifest(PinSlug(dto.Manifest, slug), draft.Secrets);

        draft.LastModifiedBy = userId;
        draft.LastModifiedByName = userName;
        draft.LastModifiedAt = time.GetUtcNow();
        draft.Version++;
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    public async Task<ErrorOr<Deleted>> DeleteAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        session.Delete(draft);
        await ClearPointerIfActiveAsync(id, userId, ct);
        await session.SaveChangesAsync(ct);
        return Result.Deleted;
    }

    /// <summary>Own pointer housekeeping on apply/delete; other admins' pointers at a
    /// shared draft heal lazily in <see cref="GetActiveAsync"/>.</summary>
    private async Task ClearPointerIfActiveAsync(Guid draftId, Guid userId, CancellationToken ct)
    {
        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId != draftId) return;
        pointer.ActiveDraftId = null;
        session.Store(pointer);
    }

    /// <summary>Clears one staged secret slot (write-only fields have no other way
    /// to be un-staged from the UI).</summary>
    public async Task<ErrorOr<RealmDraftDto>> ClearSecretAsync(
        Guid id, string slot, Guid userId, string userName, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        if (!draft.Secrets.Remove(slot))
            return Error.NotFound("Draft.SecretSlotNotFound", $"No staged secret at '{slot}'.");
        draft.LastModifiedBy = userId;
        draft.LastModifiedByName = userName;
        draft.LastModifiedAt = time.GetUtcNow();
        draft.Version++;
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    // ── Active draft (ADR-0017: implicit branches) ───────────────────────────────

    /// <summary>The admin's active draft, or null. Lazily heals a pointer whose
    /// draft was applied/deleted or turned invisible.</summary>
    public async Task<RealmDraftDto?> GetActiveAsync(Guid userId, CancellationToken ct)
    {
        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId is not { } draftId) return null;
        var draft = await LoadVisibleAsync(draftId, userId, ct);
        if (draft is null)
        {
            pointer.ActiveDraftId = null;
            session.Store(pointer);
            await session.SaveChangesAsync(ct);
            return null;
        }
        return ToDto(draft, userId);
    }

    /// <summary>Parking = branch switch away: the pointer clears, the draft stays.</summary>
    public async Task ParkAsync(Guid userId, CancellationToken ct)
    {
        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId is null) return;
        pointer.ActiveDraftId = null;
        session.Store(pointer);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>Checkout of an existing (visible) draft.</summary>
    public async Task<ErrorOr<RealmDraftDto>> SwitchAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        session.Store(new RealmDraftPointer { Id = userId, ActiveDraftId = id });
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    /// <summary>
    /// The staging seam (ADR-0017 Increment A/B): upserts ONE entity into the active
    /// draft's manifest — the "commit". With no active draft, one is created
    /// implicitly (auto-named, manifest = baseline = current export) and made active;
    /// the admin never creates a draft explicitly. The entity's natural key is
    /// computed server-side; secrets inside are extracted into write-only slots.
    /// </summary>
    public async Task<ErrorOr<RealmDraftDto>> StageEntityAsync(
        string section, JsonObject entity, string slug, Guid userId, string userName, CancellationToken ct)
    {
        if (DraftSectionRegistry.Resolve(section) is not { } meta)
            return Error.Validation("Draft.UnknownSection", $"Unknown manifest section '{section}'.");
        var key = meta.Key(entity);
        if (string.IsNullOrEmpty(key))
            return Error.Validation("Draft.KeyMissing",
                "The entity needs its natural key (slug / name / id) before it can be staged.");

        var draftResult = await GetOrCreateActiveAsync(slug, userId, userName, ct);
        if (draftResult.IsError) return draftResult.Errors;
        var draft = draftResult.Value;

        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(draft.Manifest, json)!.AsObject();
        if (meta.Collection is null)
        {
            root["Settings"] = entity.DeepClone();
        }
        else
        {
            if (root[meta.Collection] is not JsonArray list)
                root[meta.Collection] = list = [];
            var index = list.OfType<JsonObject>().ToList()
                .FindIndex(e => meta.Key(e) == key);
            if (index >= 0) list[index] = entity.DeepClone();
            else list.Add(entity.DeepClone());
        }

        // Re-staging an entity revives it: a pending deletion of the same key is moot.
        draft.Deletions.RemoveAll(d => d.Section == section && d.Key == key);

        draft.Manifest = SanitizeManifest(
            PinSlug(root.Deserialize<RealmManifest>(json)!, slug), draft.Secrets);
        Touch(draft, userId, userName);
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    /// <summary>
    /// Stages the DELETION of one live entity (ADR-0017 staged deletes): removes it
    /// from the active draft's manifest AND records the (section, key) so plan/apply
    /// treat it as a targeted delete — prune's per-entity counterpart. With no active
    /// draft, one is created implicitly (same as <see cref="StageEntityAsync"/>).
    /// </summary>
    public async Task<ErrorOr<RealmDraftDto>> StageDeleteAsync(
        string section, string key, string slug, Guid userId, string userName, CancellationToken ct)
    {
        if (DraftSectionRegistry.Resolve(section) is not { } meta || meta.Collection is null)
            return Error.Validation("Draft.UnknownSection",
                $"Unknown or non-deletable manifest section '{section}'.");
        if (string.IsNullOrEmpty(key))
            return Error.Validation("Draft.KeyMissing", "A deletion needs the entity's natural key.");

        var draftResult = await GetOrCreateActiveAsync(slug, userId, userName, ct);
        if (draftResult.IsError) return draftResult.Errors;
        var draft = draftResult.Value;

        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(draft.Manifest, json)!.AsObject();
        if (root[meta.Collection] is JsonArray list)
        {
            var index = list.OfType<JsonObject>().ToList().FindIndex(e => meta.Key(e) == key);
            if (index >= 0) list.RemoveAt(index);
        }

        if (!draft.Deletions.Any(d => d.Section == section && d.Key == key))
            draft.Deletions.Add(new RealmDraftDeletion(section, key));

        draft.Manifest = SanitizeManifest(root.Deserialize<RealmManifest>(json)!, draft.Secrets);
        Touch(draft, userId, userName);
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    /// <summary>Undoes a staged deletion: drops the (section, key) entry and restores
    /// the entity from the baseline into the manifest, so the plan reads "unchanged"
    /// again (the entity may since have changed live — that surfaces as a regular
    /// staleOverwrite conflict, exactly like any other stale draft value).</summary>
    public async Task<ErrorOr<RealmDraftDto>> UnstageDeleteAsync(
        string section, string key, string slug, Guid userId, string userName, CancellationToken ct)
    {
        if (DraftSectionRegistry.Resolve(section) is not { } meta || meta.Collection is null)
            return Error.Validation("Draft.UnknownSection",
                $"Unknown or non-deletable manifest section '{section}'.");

        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId is not { } draftId) return NotFound;
        var draft = await LoadVisibleAsync(draftId, userId, ct);
        if (draft is null) return NotFound;

        if (draft.Deletions.RemoveAll(d => d.Section == section && d.Key == key) == 0)
            return Error.NotFound("Draft.DeletionNotStaged",
                $"No staged deletion for '{key}' in section '{section}'.");

        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(draft.Manifest, json)!.AsObject();
        var baselineRoot = JsonSerializer.SerializeToNode(draft.Baseline, json)!.AsObject();
        if (baselineRoot[meta.Collection] is JsonArray baselineList &&
            baselineList.OfType<JsonObject>().FirstOrDefault(e => meta.Key(e) == key) is { } restored)
        {
            if (root[meta.Collection] is not JsonArray list)
                root[meta.Collection] = list = [];
            if (!list.OfType<JsonObject>().Any(e => meta.Key(e) == key))
                list.Add(restored.DeepClone());
        }

        draft.Manifest = SanitizeManifest(
            PinSlug(root.Deserialize<RealmManifest>(json)!, slug), draft.Secrets);
        Touch(draft, userId, userName);
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    /// <summary>Removes one entity from the active draft's manifest (staged delete /
    /// undo of a staged create). No active draft = nothing to remove.</summary>
    public async Task<ErrorOr<RealmDraftDto>> UnstageEntityAsync(
        string section, string key, string slug, Guid userId, string userName, CancellationToken ct)
    {
        if (DraftSectionRegistry.Resolve(section) is not { } meta || meta.Collection is null)
            return Error.Validation("Draft.UnknownSection", $"Unknown or non-removable manifest section '{section}'.");

        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId is not { } draftId) return NotFound;
        var draft = await LoadVisibleAsync(draftId, userId, ct);
        if (draft is null) return NotFound;

        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(draft.Manifest, json)!.AsObject();
        if (root[meta.Collection] is JsonArray list)
        {
            var index = list.OfType<JsonObject>().ToList().FindIndex(e => meta.Key(e) == key);
            if (index >= 0) list.RemoveAt(index);
        }

        draft.Manifest = SanitizeManifest(root.Deserialize<RealmManifest>(json)!, draft.Secrets);
        Touch(draft, userId, userName);
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    private async Task<ErrorOr<RealmDraft>> GetOrCreateActiveAsync(
        string slug, Guid userId, string userName, CancellationToken ct)
    {
        var pointer = await session.LoadAsync<RealmDraftPointer>(userId, ct);
        if (pointer?.ActiveDraftId is { } draftId &&
            await LoadVisibleAsync(draftId, userId, ct) is { } active)
            return active;

        var exportResult = await exporter.ExportRealmAsync(slug, ct);
        if (exportResult.IsError) return exportResult.Errors;
        var baseline = exportResult.Value;

        var now = time.GetUtcNow();
        var draft = new RealmDraft
        {
            Id = Guid.NewGuid(),
            // Generated name (ADR: author + timestamp) — renameable later via update.
            Name = $"{userName} · {now:yyyy-MM-dd HH:mm}",
            Manifest = baseline,
            Baseline = baseline,
            CreatedBy = userId,
            CreatedByName = userName,
            CreatedAt = now,
            LastModifiedBy = userId,
            LastModifiedByName = userName,
            LastModifiedAt = now,
            Version = 1,
        };
        session.Store(draft);
        session.Store(new RealmDraftPointer { Id = userId, ActiveDraftId = draft.Id });
        return draft;
    }

    private void Touch(RealmDraft draft, Guid userId, string userName)
    {
        draft.LastModifiedBy = userId;
        draft.LastModifiedByName = userName;
        draft.LastModifiedAt = time.GetUtcNow();
        draft.Version++;
    }

    /// <summary>
    /// Rebases the draft onto the CURRENT live state: baseline := fresh export. This
    /// is the "keep mine" side of conflict resolution — after per-field "take live"
    /// edits, rebasing declares every remaining draft-vs-live difference intentional,
    /// so bothChanged/staleOverwrite conflicts clear while the staged changes stay.
    /// </summary>
    public async Task<ErrorOr<RealmDraftDto>> RebaseAsync(
        Guid id, string slug, Guid userId, string userName, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;

        var exportResult = await exporter.ExportRealmAsync(slug, ct);
        if (exportResult.IsError) return exportResult.Errors;

        draft.Baseline = exportResult.Value;
        draft.LastModifiedBy = userId;
        draft.LastModifiedByName = userName;
        draft.LastModifiedAt = time.GetUtcNow();
        draft.Version++;
        session.Store(draft);
        await session.SaveChangesAsync(ct);
        return ToDto(draft, userId);
    }

    // ── Plan / apply ─────────────────────────────────────────────────────────────

    public async Task<ErrorOr<RealmPlanResult>> PlanAsync(
        Guid id, bool prune, Guid userId, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        var manifest = MergeSecrets(draft.Manifest, draft.Secrets);
        return await planner.PlanAsync(manifest, prune, draft.Baseline, draft.Deletions, ct);
    }

    /// <summary>
    /// Applies the draft — gated per ADR-0017: the plan runs first (fail-fast
    /// pre-validation, nothing written) and the apply is refused while it reports
    /// apply-errors or unresolved three-way conflicts. On success the consumed
    /// draft is deleted.
    /// </summary>
    public async Task<ErrorOr<RealmDraftApplyResult>> ApplyAsync(
        Guid id, bool prune, Guid userId, CancellationToken ct)
    {
        var draft = await LoadVisibleAsync(id, userId, ct);
        if (draft is null) return NotFound;
        var manifest = MergeSecrets(draft.Manifest, draft.Secrets);

        var planResult = await planner.PlanAsync(manifest, prune, draft.Baseline, draft.Deletions, ct);
        if (planResult.IsError) return planResult.Errors;
        var plan = planResult.Value;
        var hasErrors = plan.Sections.Any(s => s.Entries.Any(e => e.Action == "error"));
        if (hasErrors || plan.HasConflicts)
            return new RealmDraftApplyResult { Refused = true, Plan = plan };

        var applyResult = await applier.UpdateRealmAsync(manifest, prune, draft.Deletions, ct);
        if (applyResult.IsError) return applyResult.Errors;

        session.Delete(draft);
        await ClearPointerIfActiveAsync(id, userId, ct);
        await session.SaveChangesAsync(ct);
        return new RealmDraftApplyResult { Refused = false, Result = applyResult.Value };
    }

    // ── Secret slots ─────────────────────────────────────────────────────────────

    /// <summary>Moves secret values out of the manifest into encrypted slots and
    /// drops slots whose target entity no longer exists. Works on the JSON shape so
    /// it never needs to know whether a DTO is a record or a class.</summary>
    private RealmManifest SanitizeManifest(RealmManifest manifest, Dictionary<string, string> secrets)
    {
        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(manifest, json)!.AsObject();
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var validSlots = new HashSet<string>(StringComparer.Ordinal);

        Extract(root["Users"], u => (u["Key"] ?? u["UserName"] ?? u["Email"])?.GetValue<string>(),
            "users", "Password");
        Extract(root["Clients"], c => c["ClientId"]?.GetValue<string>(), "clients", "ClientSecret");
        Extract(root["LoginProviders"], p => p["Slug"]?.GetValue<string>(), "loginProviders", "ClientSecret");

        if (root["Settings"] is JsonObject settings &&
            settings["SelfRegistration"] is JsonObject selfReg)
        {
            const string captchaSlot = "settings/SelfRegistration/CaptchaSecret";
            if (selfReg["CaptchaSecret"] is JsonValue captcha &&
                captcha.GetValue<string>() is { Length: > 0 } captchaValue)
            {
                secrets[captchaSlot] = protector.Protect(captchaValue);
                // Remove (don't null) the extracted value: under v2 merge-patch an
                // explicit null IS a staged clear, while absent = unchanged — the
                // slot is the "secret staged" marker, not the manifest field.
                selfReg.Remove("CaptchaSecret");
            }
            if (secrets.ContainsKey(captchaSlot)) validSlots.Add(captchaSlot);
        }

        foreach (var stale in secrets.Keys.Where(k => !validSlots.Contains(k)).ToList())
            secrets.Remove(stale);

        return root.Deserialize<RealmManifest>(json)!;

        void Extract(JsonNode? array, Func<JsonObject, string?> key, string section, string field)
        {
            if (array is not JsonArray items) return;
            foreach (var node in items)
            {
                if (node is not JsonObject entity) continue;
                var entityKey = key(entity);
                if (string.IsNullOrEmpty(entityKey)) continue;
                var slot = $"{section}/{entityKey}/{field}";
                if (entity[field] is JsonValue value &&
                    value.GetValue<string>() is { Length: > 0 } secret)
                {
                    secrets[slot] = protector.Protect(secret);
                    // Absent, not null — see the captcha-slot comment above.
                    entity.Remove(field);
                }
                if (secrets.ContainsKey(slot)) validSlots.Add(slot);
            }
        }
    }

    /// <summary>Injects the decrypted staged secrets back into an in-memory copy of
    /// the manifest for plan/apply. Never persisted.</summary>
    private RealmManifest MergeSecrets(RealmManifest manifest, IReadOnlyDictionary<string, string> secrets)
    {
        if (secrets.Count == 0) return manifest;
        var json = jsonOptions.Value.SerializerOptions;
        var root = JsonSerializer.SerializeToNode(manifest, json)!.AsObject();
        var protector = dataProtection.CreateProtector(ProtectorPurpose);

        foreach (var (slot, encrypted) in secrets)
        {
            var parts = slot.Split('/', 3);
            if (parts.Length != 3) continue;
            var value = protector.Unprotect(encrypted);
            if (parts[0] == "settings")
            {
                if (root["Settings"] is JsonObject settings &&
                    settings[parts[1]] is JsonObject sub)
                    sub[parts[2]] = value;
                continue;
            }
            var array = parts[0] switch
            {
                "users" => root["Users"] as JsonArray,
                "clients" => root["Clients"] as JsonArray,
                "loginProviders" => root["LoginProviders"] as JsonArray,
                _ => null,
            };
            var target = array?.OfType<JsonObject>().FirstOrDefault(e =>
                ((e["Key"] ?? (parts[0] == "users" ? e["UserName"] ?? e["Email"]
                    : parts[0] == "clients" ? e["ClientId"] : e["Slug"]))
                 ?.GetValue<string>()) == parts[1]);
            if (target is not null) target[parts[2]] = value;
        }

        return root.Deserialize<RealmManifest>(json)!;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<RealmDraft?> LoadVisibleAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var draft = await session.LoadAsync<RealmDraft>(id, ct);
        return draft is null || (!draft.Shared && draft.CreatedBy != userId) ? null : draft;
    }

    private static RealmManifest PinSlug(RealmManifest manifest, string slug)
        => manifest with { Realm = manifest.Realm with { Slug = slug } };

    private static RealmDraftSummaryDto Summary(RealmDraft d, Guid userId) => new(
        d.Id, d.Name, d.Shared, d.CreatedBy == userId,
        d.CreatedByName, d.CreatedAt, d.LastModifiedByName, d.LastModifiedAt, d.Version);

    private static RealmDraftDto ToDto(RealmDraft d, Guid userId) => new(
        d.Id, d.Name, d.Shared, d.CreatedBy == userId,
        d.CreatedByName, d.CreatedAt, d.LastModifiedByName, d.LastModifiedAt, d.Version,
        d.Manifest, d.Secrets.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        [.. d.Deletions]);
}

/// <summary>Apply outcome: either the manifest went through (Result set) or the
/// gate refused it and the plan explains why (errors / unresolved conflicts).</summary>
public sealed record RealmDraftApplyResult
{
    public required bool Refused { get; init; }
    public RealmPlanResult? Plan { get; init; }
    public RealmImportResult? Result { get; init; }
}
