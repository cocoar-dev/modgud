using Cocoar.Auth.Authorization.Resources;

namespace Cocoar.Auth.Authorization.Setup;

/// <summary>
/// Options surface exposed to <c>AddCocoarAuthAuthorization</c>. Today this only
/// collects resource declarations — the principal hierarchy (<c>Person</c> +
/// <c>Group</c>) is hardcoded in <see cref="MartenStoreOptionsExtensions"/>.
/// </summary>
public class AuthorizationOptions
{
    private readonly ResourceRegistry _resourceRegistry = new();

    internal ResourceRegistry ResourceRegistry => _resourceRegistry;

    /// <summary>
    /// Declares a resource type and the actions it supports. Permissions follow
    /// the convention <c>"resource:action"</c> — a role with <c>ResourceType="todo"</c> and
    /// <c>Permissions=["read","update"]</c> resolves to <c>todo:read</c> and <c>todo:update</c>.
    /// </summary>
    public AuthorizationOptions RegisterResource(string resourceType, params string[] actions)
    {
        _resourceRegistry.Register(resourceType, actions);
        return this;
    }
}
