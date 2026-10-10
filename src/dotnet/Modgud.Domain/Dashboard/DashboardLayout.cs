namespace Modgud.Domain.Dashboard;

/// <summary>
/// A stored dashboard arrangement: which widgets, in which order, how wide.
/// Two kinds share this document, told apart by <see cref="Id"/>:
/// <list type="bullet">
///   <item><description><see cref="RealmDefaultId"/> — the realm's default, set by an admin.</description></item>
///   <item><description><see cref="UserId(Guid)"/> — one user's own arrangement.</description></item>
/// </list>
/// The dashboard resolves user layout → realm default → the default built into
/// the frontend, so a realm with neither document still shows a dashboard.
///
/// <para><b>A layout grants nothing.</b> Widget ids are opaque to the server; the
/// catalog lives in the frontend, and each widget's data is gated by the
/// permission on the endpoint that serves it. A layout naming a widget the viewer
/// may not see simply renders without it.</para>
///
/// Persistence binding lives in <c>MartenConfiguration</c> so Domain stays free
/// of Marten attributes.
/// </summary>
public sealed class DashboardLayout
{
    public const string RealmDefaultId = "realm-default";

    /// <summary>Upper bound on stored placements — far above any real catalog.</summary>
    public const int MaxWidgets = 60;

    public static string UserId(Guid userId) => $"user:{userId:N}";

    public string Id { get; set; } = "";

    public List<DashboardWidgetPlacement> Widgets { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One widget on the dashboard. List order is display order.</summary>
public sealed record DashboardWidgetPlacement(string Widget, string Size)
{
    /// <summary>Column spans the grid understands (see the frontend's size map).</summary>
    public static readonly IReadOnlySet<string> Sizes =
        new HashSet<string>(StringComparer.Ordinal) { "xs", "s", "m", "l", "xl", "full" };
}
