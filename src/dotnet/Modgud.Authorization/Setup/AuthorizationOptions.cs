using Modgud.Authorization.Resources;

namespace Modgud.Authorization.Setup;

/// <summary>
/// Options surface exposed to <c>AddModgudAuthorization</c>. Today this only
/// collects resource declarations — the principal hierarchy (<c>Person</c> +
/// <c>Group</c>) is hardcoded in <see cref="MartenStoreOptionsExtensions"/>.
/// </summary>
public class AuthorizationOptions
{
    private readonly ResourceRegistry _resourceRegistry = new();

    internal ResourceRegistry ResourceRegistry => _resourceRegistry;

    /// <summary>
    /// Declares a resource type within an app and the actions it supports.
    /// Permissions follow the convention <c>"appSlug:resource:action"</c> —
    /// a role with <c>AppSlug="acme-tasks"</c>, <c>ResourceType="todo"</c>, and
    /// <c>Permissions=["read","update"]</c> resolves to <c>acme-tasks:todo:read</c>
    /// and <c>acme-tasks:todo:update</c>.
    /// </summary>
    public AuthorizationOptions RegisterResource(string appSlug, string resourceType, params string[] actions)
    {
        _resourceRegistry.Register(appSlug, resourceType, actions);
        return this;
    }
}
