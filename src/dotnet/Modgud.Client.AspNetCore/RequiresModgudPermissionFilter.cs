using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modgud.Client.AspNetCore;

/// <summary>
/// Endpoint filter that gates a Minimal-API endpoint on a Modgud
/// permission. Reads the <c>"permission"</c> claims that
/// <see cref="ModgudClaimsTransformation"/> stamped on the principal
/// (flattened from <c>resource_access[<see cref="ModgudOptions.Audience"/>].permissions</c>)
/// and does a pure <c>contains</c>-check against the requested string.
///
/// <para>The IdP already pre-expanded bypass tiers (<c>realm:admin</c> →
/// every catalog string of every reachable App; <c>&lt;r&gt;:admin</c> →
/// every <c>&lt;r&gt;:&lt;a&gt;</c> in the App's catalog) before emission, so
/// no <c>PermissionEvaluator</c> dance is needed here — the filter is a
/// straight membership test.</para>
///
/// <para>Synchronous (no I/O). Returns <c>401</c> when anonymous,
/// <c>403</c> when authenticated but lacking the permission.</para>
/// </summary>
public sealed class RequiresModgudPermissionFilter : IEndpointFilter
{
    private readonly string _permission;

    public RequiresModgudPermissionFilter(string permission)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        _permission = permission;
    }

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
            return ValueTask.FromResult<object?>(Results.Unauthorized());

        var hasPermission = user
            .FindAll(ModgudClaimsTransformation.PermissionClaimType)
            .Any(c => string.Equals(c.Value, _permission, StringComparison.Ordinal));

        if (!hasPermission)
            return ValueTask.FromResult<object?>(Results.Forbid());

        return next(context);
    }
}

public static class RequiresModgudPermissionExtensions
{
    /// <summary>
    /// Gates the route group on the given permission. Equivalent to wiring
    /// <see cref="RequiresModgudPermissionFilter"/> as an endpoint filter.
    /// The permission is bare 2-segment (<c>"&lt;resource&gt;:&lt;action&gt;"</c>) —
    /// the App context is implicit from the audience the lib was configured
    /// with.
    /// </summary>
    public static RouteGroupBuilder RequiresModgudPermission(this RouteGroupBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new RequiresModgudPermissionFilter(permission));
        return builder;
    }

    /// <summary>Per-endpoint variant of <see cref="RequiresModgudPermission(RouteGroupBuilder,string)"/>.</summary>
    public static RouteHandlerBuilder RequiresModgudPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.AddEndpointFilter(new RequiresModgudPermissionFilter(permission));
        return builder;
    }
}
