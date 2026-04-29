namespace Cocoar.Auth.Authorization.Roles;

/// <summary>
/// Named set of permissions bound to a specific resource type within an app.
/// Roles define the <b>what</b> (allowed actions) — they don't decide
/// <b>which rows</b> a principal can see. Row visibility is the access script's
/// job on the <see cref="Principals.Group"/>.
/// </summary>
public class PermissionRole
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// The app this role belongs to (e.g. <c>"cocoar-auth"</c>, <c>"timetodo"</c>).
    /// A role's resources/actions are interpreted within this app's namespace,
    /// and the role only contributes to permission resolution when the
    /// requesting app matches.
    /// </summary>
    public string AppSlug { get; set; } = "";

    /// <summary>
    /// The resource this role scopes its actions to — e.g. <c>"todo"</c>, <c>"customer"</c>,
    /// <c>"user"</c>. Actions in <see cref="Permissions"/> are interpreted relative
    /// to it: <c>["read", "update"]</c> against <c>AppSlug="timetodo"</c> +
    /// <c>ResourceType="todo"</c> ⇒ <c>timetodo:todo:read</c>, <c>timetodo:todo:update</c>.
    /// </summary>
    public string ResourceType { get; set; } = "";

    /// <summary>
    /// Bare action names (e.g. <c>"read"</c>, <c>"update"</c>) — the
    /// app-slug + resource-type prefix is applied at permission-check time.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    public bool IsDeleted { get; set; }
}
