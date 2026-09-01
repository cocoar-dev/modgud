namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// A named, server-persisted configuration draft (ADR-0005 Phase 1) — the staging
/// document behind the draft workspace. Lives in the TENANT database, so tenancy
/// isolation comes for free.
///
/// <para><see cref="Manifest"/> is stored SANITIZED: secret-bearing fields (user
/// passwords, client/provider secrets, the captcha secret) are stripped on every
/// write and kept DataProtection-encrypted in <see cref="Secrets"/>, keyed by a
/// stable slot path (e.g. <c>users/maria/Password</c>). Reads therefore never echo
/// a staged secret — the UI only learns WHICH slots are set. Plan and apply merge
/// the decrypted secrets back in memory.</para>
///
/// <para><see cref="Baseline"/> is the realm export taken when the draft was
/// created — the anchor for the three-way conflict classification (exports are
/// secret-free by construction). <see cref="Version"/> implements optimistic
/// concurrency for collaborative editing: an update carries the version it was
/// based on and is refused when the draft moved on.</para>
/// </summary>
public sealed class RealmDraft
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The staged manifest, secret fields stripped.</summary>
    public RealmManifest Manifest { get; set; } = null!;

    /// <summary>Export snapshot from draft creation — three-way conflict anchor.</summary>
    public RealmManifest Baseline { get; set; } = null!;

    /// <summary>Secret slot path → DataProtection-encrypted value.</summary>
    public Dictionary<string, string> Secrets { get; set; } = [];

    /// <summary>False (default): only the creator sees and edits the draft.
    /// True: every realm admin can see, edit and apply it (collaboration).</summary>
    public bool Shared { get; set; }

    public Guid CreatedBy { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public Guid LastModifiedBy { get; set; }
    public string LastModifiedByName { get; set; } = string.Empty;
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>Optimistic-concurrency counter; bumped on every content update.</summary>
    public int Version { get; set; }
}

/// <summary>
/// Per-admin active-draft pointer (ADR-0005: "each admin has an active-draft
/// pointer"). Document id = the admin's user id; lives in the tenant DB so the
/// pointer is per realm automatically. Parking clears <see cref="ActiveDraftId"/>
/// (the draft itself stays); the pointer also clears lazily when the draft it
/// references was applied or deleted.
/// </summary>
public sealed class RealmDraftPointer
{
    public Guid Id { get; set; }
    public Guid? ActiveDraftId { get; set; }
}

/// <summary>List row for the draft picker.</summary>
public sealed record RealmDraftSummaryDto(
    Guid Id,
    string Name,
    bool Shared,
    bool Mine,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    string LastModifiedByName,
    DateTimeOffset LastModifiedAt,
    int Version);

/// <summary>Full draft for the workspace. The manifest is sanitized; the staged
/// secrets are only visible as their slot paths.</summary>
public sealed record RealmDraftDto(
    Guid Id,
    string Name,
    bool Shared,
    bool Mine,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    string LastModifiedByName,
    DateTimeOffset LastModifiedAt,
    int Version,
    RealmManifest Manifest,
    List<string> SecretSlots);

public sealed record CreateRealmDraftDto
{
    public required string Name { get; init; }

    /// <summary>"export" (default) — start from the current realm export;
    /// "empty" — start blank; "manifest" — start from <see cref="Manifest"/>
    /// (upload / AI-generated). The baseline is ALWAYS the current export.</summary>
    public string Source { get; init; } = "export";

    public RealmManifest? Manifest { get; init; }
}

public sealed record UpdateRealmDraftDto
{
    /// <summary>The draft version this update was based on — refused with 409 when
    /// the draft has moved on (collaborative editing).</summary>
    public required int ExpectedVersion { get; init; }

    public string? Name { get; init; }
    public bool? Shared { get; init; }

    /// <summary>New manifest content; omitted = keep. Secret fields inside are
    /// extracted into the encrypted secret slots and never stored in plain form.</summary>
    public RealmManifest? Manifest { get; init; }
}
