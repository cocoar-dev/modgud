namespace Cocoar.Auth.Authorization.Apps;

/// <summary>
/// Logical scope inside a realm. Each <see cref="App"/> owns a set of resources
/// and is the namespace for permission strings shaped as
/// <c>{AppSlug}:{Resource}:{Action}</c>.
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
    /// Permission strings reference this slug as their first segment.
    /// </summary>
    public string Slug { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// Resources this app defines (e.g. <c>["todo", "project"]</c>). Permissions
    /// are validated against the (slug, resource) pair in the
    /// <c>ResourceRegistry</c>.
    /// </summary>
    public List<string> Resources { get; set; } = [];

    /// <summary>
    /// System apps (currently only <c>cocoar-auth</c>) cannot be deleted. The
    /// flag is set on creation and never changes.
    /// </summary>
    public bool IsSystem { get; set; }

    public bool IsDeleted { get; set; }
}
