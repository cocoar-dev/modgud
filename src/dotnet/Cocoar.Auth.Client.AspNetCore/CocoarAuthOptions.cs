namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Configuration for the Cocoar.Auth resource-server integration.
/// </summary>
public sealed class CocoarAuthOptions
{
    /// <summary>
    /// The slug of the App this resource server represents (e.g. <c>"timetodo"</c>).
    /// The claims-transformation reads <c>resource_access[AppSlug].roles</c>
    /// from the JWT and surfaces them as flat <c>ClaimTypes.Role</c> claims
    /// so <c>[Authorize(Roles="...")]</c> sees them.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string AppSlug { get; set; } = string.Empty;

    /// <summary>
    /// JSON property name carrying the Keycloak-style nested role map.
    /// Default: <c>"resource_access"</c> — matches Cocoar.Auth and
    /// Keycloak. Override only if you target a custom IdP that emits the
    /// same shape under a different key.
    /// </summary>
    public string ResourceAccessClaimName { get; set; } = "resource_access";

    /// <summary>
    /// JSON property name carrying the flat group-names array. Default
    /// <c>"groups"</c>. The transformation copies these into a flat
    /// <c>"group"</c> claim type for symmetric consumption alongside roles.
    /// </summary>
    public string GroupsClaimName { get; set; } = "groups";
}
