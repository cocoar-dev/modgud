namespace Modgud.Application.DTOs.Assets;

/// <summary>Read shape for the asset library. Excludes the binary payload —
/// the public <c>/assets/{id}</c> endpoint serves that on demand.</summary>
public record AssetDto
{
    public string Id { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public DateTimeOffset UploadedAt { get; init; }
    public string? UploadedByUsername { get; init; }
    /// <summary>Public URL the SPA can drop straight into <c>&lt;img src&gt;</c>.</summary>
    public string Url { get; init; } = string.Empty;
}

/// <summary>Returned by DELETE when the asset is still referenced. The
/// SPA shows the references list so the admin can clear them first.</summary>
public record AssetInUseDto
{
    public string Id { get; init; } = string.Empty;
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
}
