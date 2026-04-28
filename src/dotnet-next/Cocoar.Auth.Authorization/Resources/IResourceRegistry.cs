namespace Cocoar.Auth.Authorization.Resources;

/// <summary>
/// Tracks which resource types exist in the app and which actions each supports.
/// Apps register their resources at startup via the fluent builder in the DI
/// setup. Consumed by permission validation (role.Permissions must reference
/// known actions) and by the script-editor TypeScript-definition generator.
/// <para>
/// Permissions follow the convention <c>"resource:action"</c>. A role stores bare
/// action names (<c>"read"</c>) against a resource type (<c>"todo"</c>); the full
/// permission is <c>"todo:read"</c>.
/// </para>
/// </summary>
public interface IResourceRegistry
{
    bool IsValidPermission(string permission);
    bool IsValidAction(string resourceType, string action);
    IReadOnlyList<string> GetAllPermissions();
    IReadOnlyList<string> GetActionsForResource(string resourceType);
    IReadOnlyList<string> GetResourceTypes();
}
