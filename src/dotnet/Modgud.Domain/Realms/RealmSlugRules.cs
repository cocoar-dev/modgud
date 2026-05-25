using System.Text.RegularExpressions;

namespace Modgud.Domain.Realms;

/// <summary>
/// Rules for validating <see cref="Realm.Slug"/> values. Pure, statically usable, and
/// the single source of truth — both the realm-provisioning service and any UI form
/// validation should call into here so the rules don't drift.
///
/// <para>A valid slug:</para>
/// <list type="bullet">
///   <item>is 3 to 63 characters total</item>
///   <item>starts with a lowercase letter</item>
///   <item>ends with a lowercase letter or digit</item>
///   <item>contains only lowercase letters, digits, and hyphens</item>
/// </list>
///
/// <para>Reserved slugs are rejected separately — they collide with system paths
/// (<c>health</c>, <c>swagger</c>, <c>openapi</c>, <c>_framework</c>) or the
/// system tenant (<c>system</c>).</para>
/// </summary>
public static partial class RealmSlugRules
{
    /// <summary>
    /// The slug of the deployment's single Control-Plane realm. Reserved
    /// in <see cref="ReservedSlugs"/>, immutable, and the architectural
    /// anchor for cross-realm administration: <c>/api/admin/realms/*</c>
    /// endpoints, the <c>control-plane:*</c> permission namespace, and
    /// the routing-gate that hides those surfaces from tenant realms.
    /// </summary>
    public const string SystemSlug = "system";

    public static IReadOnlySet<string> ReservedSlugs { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SystemSlug, "health", "swagger", "openapi", "_framework",
        };

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.Compiled)]
    private static partial Regex SlugFormatRegex();

    /// <summary>True if <paramref name="slug"/> matches the format rules. Whitespace and null fail.</summary>
    public static bool IsValidFormat(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugFormatRegex().IsMatch(slug);

    /// <summary>True if <paramref name="slug"/> is on the reserved list (case-insensitive).</summary>
    public static bool IsReserved(string? slug) =>
        slug is not null && ReservedSlugs.Contains(slug);
}
