namespace Modgud.Authorization.Resources;

/// <summary>
/// Tracks which resource types exist per app and which actions each supports.
/// Apps register their resources at startup via the fluent builder in the DI
/// setup. Consumed by permission validation (role.Permissions must reference
/// known actions) and by the script-editor TypeScript-definition generator.
/// <para>
/// Permissions follow the convention <c>"appSlug:resource:action"</c>. A role
/// stores a single <c>AppSlug</c> + <c>ResourceType</c> + bare action names
/// (<c>"read"</c>); the full permission is reconstructed at evaluation time as
/// <c>"acme-tasks:todo:read"</c>.
/// </para>
/// </summary>
public interface IResourceRegistry
{
    /// <summary>
    /// Validates a fully-qualified permission string of shape
    /// <c>"appSlug:resource:action"</c>.
    /// </summary>
    bool IsValidPermission(string permission);

    /// <summary>
    /// Validates a (app, resource, action) triple — used when checking a
    /// role's bare action against the registry.
    /// </summary>
    bool IsValidAction(string appSlug, string resourceType, string action);

    /// <summary>
    /// Returns every registered permission across every app, fully qualified
    /// as <c>"appSlug:resource:action"</c>.
    /// </summary>
    IReadOnlyList<string> GetAllPermissions();

    /// <summary>
    /// Returns the actions registered for a given (app, resource) pair, or
    /// an empty list if the pair is unknown.
    /// </summary>
    IReadOnlyList<string> GetActionsForResource(string appSlug, string resourceType);

    /// <summary>
    /// Returns the resource types registered under the given app slug, or
    /// an empty list if the app is unknown.
    /// </summary>
    IReadOnlyList<string> GetResourceTypes(string appSlug);

    /// <summary>
    /// Returns every app slug that has at least one registered resource.
    /// </summary>
    IReadOnlyList<string> GetAppSlugs();
}
