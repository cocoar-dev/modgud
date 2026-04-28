namespace TimeToDo.Authorization.Roles;

/// <summary>
/// Named set of permissions bound to a specific resource type. Roles define the
/// <b>what</b> (allowed actions) — they don't decide <b>which rows</b> a principal
/// can see. Row visibility is the access script's job on the <see cref="Principals.Group"/>.
/// </summary>
public class PermissionRole
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// The resource this role scopes its actions to — e.g. <c>"todo"</c>, <c>"customer"</c>,
    /// <c>"app"</c>. Actions in <see cref="Permissions"/> are interpreted relative to it
    /// (<c>["read", "update"]</c> against <c>ResourceType="todo"</c> ⇒ <c>todo:read</c>, <c>todo:update</c>).
    /// </summary>
    public string ResourceType { get; set; } = "";

    /// <summary>
    /// Bare action names (e.g. <c>"read"</c>, <c>"update"</c>) — the resource-type prefix
    /// is applied at permission-check time. Legacy fully-qualified entries
    /// (<c>"resource:action"</c>) are accepted and passed through unchanged.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    public bool IsDeleted { get; set; }
}
