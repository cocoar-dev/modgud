namespace Modgud.Domain.Assets;

/// <summary>
/// Per-realm uploaded asset (logo, favicon, login illustration, …). Stored
/// in the tenant DB so a master-DB compromise can't read other tenants'
/// assets and a tenant-DB backup is self-contained.
///
/// <para>The binary payload lives in <see cref="Data"/> as BYTEA. Assets
/// are small (logos &lt; 100&#160;KB typical) and we cap at 2&#160;MB per
/// asset, so DB-blob storage is the right call for the volume —
/// transactional with branding settings, no extra filesystem mount, no
/// extra service. Larger asset shapes (per-realm media library, video,
/// etc.) would warrant object storage; this is not that.</para>
///
/// <para>Content is content-addressed via <see cref="Sha256"/>; the id is
/// a fresh GUID per upload so the public URL is stable even if the same
/// bytes get re-uploaded. Re-upload of identical bytes is allowed (admin
/// may want a second copy with a different filename) — we don't dedupe.</para>
/// </summary>
public sealed class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Original filename from the upload, kept for the admin UI.
    /// NOT used in the public URL — the URL is GUID-only so no path-
    /// traversal vector ever surfaces.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Content-Type detected from magic bytes server-side, NOT
    /// from the upload's Content-Type header (which the client controls).
    /// One of the allowlisted image MIME types.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Lowercase-hex SHA-256 of <see cref="Data"/>. Used as the
    /// ETag for HTTP cache validation and as a content fingerprint in the
    /// admin UI (e.g. "same image as the other one?").</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The raw bytes. For SVG uploads this is the SANITIZED form
    /// — the original on-the-wire bytes never get persisted unprocessed.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public DateTimeOffset UploadedAt { get; set; }

    public Guid? UploadedByUserId { get; set; }

    /// <summary>Snapshot of the uploader's username at upload time. Kept
    /// here so a later user-rename or user-delete still shows something
    /// useful in the asset list.</summary>
    public string? UploadedByUsername { get; set; }
}
