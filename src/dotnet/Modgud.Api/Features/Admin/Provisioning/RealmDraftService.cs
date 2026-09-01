using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// CRUD + plan/apply orchestration for <see cref="RealmDraft"/>s (ADR-0005 Phase 1).
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
        await session.SaveChangesAsync(ct);
        return Result.Deleted;
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
        return await planner.PlanAsync(manifest, prune, draft.Baseline, ct);
    }

    /// <summary>
    /// Applies the draft — gated per ADR-0005: the plan runs first (fail-fast
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

        var planResult = await planner.PlanAsync(manifest, prune, draft.Baseline, ct);
        if (planResult.IsError) return planResult.Errors;
        var plan = planResult.Value;
        var hasErrors = plan.Sections.Any(s => s.Entries.Any(e => e.Action == "error"));
        if (hasErrors || plan.HasConflicts)
            return new RealmDraftApplyResult { Refused = true, Plan = plan };

        var applyResult = await applier.UpdateRealmAsync(manifest, prune, ct);
        if (applyResult.IsError) return applyResult.Errors;

        session.Delete(draft);
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
                selfReg["CaptchaSecret"] = null;
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
                    entity[field] = null;
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
        d.Manifest, d.Secrets.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
}

/// <summary>Apply outcome: either the manifest went through (Result set) or the
/// gate refused it and the plan explains why (errors / unresolved conflicts).</summary>
public sealed record RealmDraftApplyResult
{
    public required bool Refused { get; init; }
    public RealmPlanResult? Plan { get; init; }
    public RealmImportResult? Result { get; init; }
}
