using System.Text.RegularExpressions;

namespace Modgud.Authorization.Apps;

/// <summary>
/// Format rules for <see cref="AppPermission"/> entries. Both
/// <see cref="AppPermission.Resource"/> and <see cref="AppPermission.Action"/>
/// must individually match the segment grammar — combined they form the
/// canonical 2-segment string <c>"&lt;resource&gt;:&lt;action&gt;"</c>.
///
/// <para>Per the design spec
/// (<c>website/dev-notes/future-features/permission-modell.md §3</c>):
/// each segment is <c>^[a-z0-9-]+$</c>, lowercase only, no slug prefix.</para>
/// </summary>
public static partial class AppPermissionRules
{
    [GeneratedRegex(@"^[a-z0-9-]+$", RegexOptions.Compiled)]
    private static partial Regex SegmentRegex();

    /// <summary>True if <paramref name="value"/> is a valid resource or action segment.</summary>
    public static bool IsValidSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SegmentRegex().IsMatch(value);

    /// <summary>
    /// Validates a <c>"&lt;resource&gt;:&lt;action&gt;"</c> string in one shot. Returns
    /// the parsed parts on success or <c>null</c> on failure.
    /// </summary>
    public static (string Resource, string Action)? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = input.Split(':');
        if (parts.Length != 2) return null;
        if (!IsValidSegment(parts[0]) || !IsValidSegment(parts[1])) return null;
        return (parts[0], parts[1]);
    }
}
