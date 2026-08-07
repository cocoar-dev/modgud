namespace Modgud.Domain.Realms;

/// <summary>
/// Realm-owned reusable PageBuilder subtree. Definitions live independently
/// from page variants; every published version is immutable and page drafts
/// pin the exact version they materialized.
/// </summary>
public class PageComposition
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public List<PageCompositionVersion> Versions { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class PageCompositionVersion
{
    public int Number { get; set; }
    /// <summary>Serialized PageBuilder element root. A page root is forbidden.</summary>
    public string Root { get; set; } = default!;
    public DateTimeOffset PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
}
