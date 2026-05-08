namespace Cocoar.Auth.Authorization.Roles;

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
///   <item><see cref="IsRealmAdmin"/> — when true, the role grants
///   <c>realm:admin</c> regardless of <see cref="AppId"/>. Reserved for the
///   System Admin role; bypasses every permission check across every realm.</item>
/// </list>
///
/// <para><see cref="AppId"/> is nullable so that a pure-realm-admin role
/// (<see cref="IsRealmAdmin"/> = true, no catalog grants) can be modelled
/// without the operator having to pick an arbitrary App. When
/// <see cref="AppId"/> is null, <see cref="PermissionIds"/> must be empty —
/// nothing to FK into.</para>
/// </summary>
public class PermissionRole
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// FK to <c>App.Id</c>. Null only for pure-realm-admin roles. When set,
    /// the role's grants are interpreted within that App's catalog.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// When true, the role grants <c>realm:admin</c> — the realm-wide bypass
    /// recognised by <see cref="Services.PermissionEvaluator"/>. Lives outside
    /// any App catalog (see permission-modell.md §3 "Sonderfall realm:admin").
    /// </summary>
    public bool IsRealmAdmin { get; set; }

    /// <summary>
    /// Subset of the role's App catalog this role grants. Each entry is an
    /// <c>AppPermission.Id</c> in <see cref="AppId"/>'s App. Empty when the
    /// role grants nothing through the catalog (only valid alongside
    /// <see cref="IsRealmAdmin"/>).
    /// </summary>
    public List<Guid> PermissionIds { get; set; } = new();

    public bool IsDeleted { get; set; }
}
