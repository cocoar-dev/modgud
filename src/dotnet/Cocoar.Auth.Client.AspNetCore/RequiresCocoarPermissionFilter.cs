using Cocoar.Auth.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Endpoint filter that gates a Minimal-API endpoint on a Cocoar.Auth
/// permission. Reads the <c>"permission"</c> claims that
/// <see cref="CocoarAuthClaimsTransformation"/> populated from the
/// distribution API and runs them through the same
/// <see cref="PermissionEvaluator"/> the IdP uses — so the resource-wide
/// <c>&lt;resource&gt;:admin</c> bypass and the <c>realm:admin</c> bypass
/// are honoured automatically.
///
/// <para>Synchronous (no I/O): the distribution call already happened
/// once per request inside the claims-transformation, this filter just
/// reads the resulting claims.</para>
///
/// <para>Returns <c>401</c> when anonymous, <c>403</c> when authenticated
/// but lacking the permission.</para>
/// </summary>
public sealed class RequiresCocoarPermissionFilter : IEndpointFilter
{
    private readonly string _permission;

    public RequiresCocoarPermissionFilter(string permission)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        _permission = permission;
    }

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
            return ValueTask.FromResult<object?>(Results.Unauthorized());

        var grants = user.FindAll(CocoarAuthClaimsTransformation.PermissionClaimType)
            .Select(c => c.Value)
            .ToArray();

        if (!PermissionEvaluator.Evaluate(grants, _permission))
            return ValueTask.FromResult<object?>(Results.Forbid());

        return next(context);
    }
}

public static class RequiresCocoarPermissionExtensions
{
    /// <summary>
    /// Gates the route group on the given permission. Equivalent to wiring
    /// <see cref="RequiresCocoarPermissionFilter"/> as an endpoint filter.
    /// The permission is bare 2-segment (<c>"&lt;resource&gt;:&lt;action&gt;"</c>) —
    /// the App context is implicit (the RS authenticates against the
    /// distribution API with its own credentials).
    /// </summary>
    public static RouteGroupBuilder RequiresCocoarPermission(this RouteGroupBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new RequiresCocoarPermissionFilter(permission));
        return builder;
    }

    /// <summary>Per-endpoint variant of <see cref="RequiresCocoarPermission(RouteGroupBuilder,string)"/>.</summary>
    public static RouteHandlerBuilder RequiresCocoarPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new RequiresCocoarPermissionFilter(permission));
        return builder;
    }
}
