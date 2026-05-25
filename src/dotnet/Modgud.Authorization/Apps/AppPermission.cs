namespace Modgud.Authorization.Apps;

/// <summary>
/// A single permission entry in an <see cref="App"/>'s catalog. The catalog
/// is the single source of truth for what permissions exist within the app —
/// what is not in the list does not exist.
///
/// <para>The <see cref="Id"/> is the stable identity. <see cref="Resource"/>
/// and <see cref="Action"/> together form the public string representation
/// (<c>"&lt;resource&gt;:&lt;action&gt;"</c>) used in role grants, distribution-API
/// responses and runtime evaluator checks. Renaming either field updates the
/// string but does not break Role / OAuthApi-Subset references that point at
/// the entry by <see cref="Id"/>.</para>
/// </summary>
public sealed record AppPermission(
    Guid Id,
    string Resource,
    string Action,
    string? Description)
{
    /// <summary>
    /// Convenience: the canonical 2-segment string form
    /// (<c>"&lt;resource&gt;:&lt;action&gt;"</c>) used in distribution-API responses
    /// and bare grants. The app-slug is intentionally NOT part of this string —
    /// the app-context is implicit from the catalog the permission lives in.
    /// </summary>
    public string ToPermissionString() => $"{Resource}:{Action}";
}
