namespace Cocoar.Auth.Authorization.Apps;

/// <summary>
/// Logical scope inside a realm. Each <see cref="App"/> owns a permission
/// catalog (<see cref="Permissions"/>) and is the namespace for permission
/// strings shaped as <c>{Resource}:{Action}</c>.
///
/// <para>This is a <b>discriminator within the realm</b>, not an isolation
/// boundary — the realm/tenant split already provides hard isolation via the
/// per-realm Marten store. Apps coexist in the same tenant DB and are
/// distinguished by <see cref="Slug"/>.</para>
///
/// <para>Cocoar.Auth itself is registered as the app <c>cocoar-auth</c>
/// (system, immutable). Other Cocoar SaaS apps (e.g. <c>timetodo</c>) get
/// their own <see cref="App"/> per realm so the realm admin can decide which
/// apps a tenant uses.</para>
///
/// <para>Naming note: the user-facing concept in docs/UI is "Application".
/// The class is named <c>App</c> internally to avoid collision with the
/// <c>Cocoar.Auth.Application</c> CQRS-layer project namespace.</para>
/// </summary>
public class App
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable, URL-safe identifier (e.g. <c>"cocoar-auth"</c>, <c>"timetodo"</c>).
    /// Used to disambiguate apps within a realm; never appears as a prefix in
    /// stored permission strings.
    /// </summary>
    public string Slug { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// The app's permission catalog — the single source of truth for what
    /// permissions exist within this app. What is not in the list does not
    /// exist. Each entry has a stable <see cref="AppPermission.Id"/> so
    /// downstream references (Role grants, OAuthApi subsets) survive
    /// resource / action renames.
    /// </summary>
    public List<AppPermission> Permissions { get; set; } = [];

    /// <summary>
    /// System apps (currently only <c>cocoar-auth</c>) cannot be deleted. The
    /// flag is set on creation and never changes.
    /// </summary>
    public bool IsSystem { get; set; }

    public bool IsDeleted { get; set; }
}
