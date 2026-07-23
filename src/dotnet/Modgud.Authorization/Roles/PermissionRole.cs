namespace Modgud.Authorization.Roles;

/// <summary>
/// Named bundle of permission grants attached to one App. Roles define the
/// <b>what</b> (allowed actions). Row-level visibility is not the IAM's
/// concern — it stays in the consuming app where the row schema lives.
///
/// <para>A role grants permissions in two ways:</para>
/// <list type="bullet">
///   <item><see cref="PermissionIds"/> — FKs into <c>App.Permissions[].Id</c>
///   of the role's <see cref="AppId"/>. Survive resource/action renames in
///   the catalog. Roles can grant any subset of their App's catalog,
///   including multiple resources within the same App.</item>
///   <item><see cref="IsRealmAdmin"/> — when true, the role has no
///   <see cref="AppId"/> and grants <c>realm:admin</c>. Reserved for the
///   System Admin role; bypasses every permission check across every App in
///   the current realm, never across realm boundaries.</item>
/// </list>
///
/// <para>These modes are mutually exclusive. An ordinary role has an
/// <see cref="AppId"/> and optional grants from that App's catalog. A
/// realm-admin role has no App link and no catalog grants.</para>
/// </summary>
public class PermissionRole
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// FK to <c>App.Id</c>. Required for ordinary roles and null for
    /// realm-admin roles. When set, the role's grants are interpreted within
    /// that App's catalog.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// When true, the role grants <c>realm:admin</c> — the current-realm-wide
    /// bypass recognised by <c>Modgud.Permissions.PermissionEvaluator</c>.
    /// Lives outside every App catalog and requires <see cref="AppId"/> and
    /// <see cref="PermissionIds"/> to be empty.
    /// </summary>
    public bool IsRealmAdmin { get; set; }

    /// <summary>
    /// Subset of the role's App catalog this role grants. Each entry is an
    /// <c>AppPermission.Id</c> in <see cref="AppId"/>'s App. Empty when the
    /// role is an ordinary App role. Always empty for a realm-admin role.
    /// </summary>
    public List<Guid> PermissionIds { get; set; } = new();

    public bool IsDeleted { get; set; }
}
