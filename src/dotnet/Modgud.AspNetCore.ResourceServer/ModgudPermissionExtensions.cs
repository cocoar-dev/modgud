using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>ASP.NET Core authorization helpers for Modgud permissions.</summary>
public static class ModgudPermissionExtensions
{
    /// <summary>
    /// Requires an authenticated principal with the exact Modgud permission.
    /// The requirement is attached as ASP.NET Core authorization metadata.
    /// </summary>
    public static RouteHandlerBuilder RequireModgudPermission(
        this RouteHandlerBuilder builder,
        string permission)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RequireAuthorization(BuildPolicy(permission));
        return builder;
    }

    /// <summary>Route-group variant of <see cref="RequireModgudPermission(RouteHandlerBuilder,string)"/>.</summary>
    public static RouteGroupBuilder RequireModgudPermission(
        this RouteGroupBuilder builder,
        string permission)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RequireAuthorization(BuildPolicy(permission));
        return builder;
    }

    internal static AuthorizationPolicy BuildPolicy(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(ModgudClaimTypes.Permission, permission)
            .Build();
    }
}
