using System.Text.RegularExpressions;

namespace Cocoar.Auth.Authorization.Apps;

/// <summary>
/// Rules for validating <see cref="App.Slug"/> values. Pure, statically usable,
/// and the single source of truth — both the App admin endpoint and any UI form
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
/// <para>Reserved slugs are rejected separately — they collide with the
/// permission grammar (<c>realm</c> is the synthetic namespace for
/// <c>realm:admin</c>) or carry special meaning in <c>Group.BoundTo</c>
/// (<c>"*"</c> means "all apps").</para>
///
/// <para><see cref="AppSlugs.CocoarAuth"/> is reserved on creation but
/// pre-seeded by <c>AppRealmSeeder</c>, so it cannot be created by an admin
/// after the fact.</para>
/// </summary>
public static partial class AppSlugRules
{
    public static IReadOnlySet<string> ReservedSlugs { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "realm",     // permission-string prefix for cross-app bypasses (realm:admin)
            "*",         // BoundTo wildcard
            AppSlugs.CocoarAuth, // system app — seeded automatically, never created
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
