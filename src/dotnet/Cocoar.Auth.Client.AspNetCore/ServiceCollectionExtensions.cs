using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Client.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cocoar.Auth claims-transformation that flattens
    /// <c>resource_access[appSlug].roles</c> into ASP.NET Core's
    /// <see cref="System.Security.Claims.ClaimTypes.Role"/> claim and the
    /// <c>groups</c> array into a flat <c>"group"</c> claim.
    ///
    /// <para>Typical usage in a resource-server <c>Program.cs</c>:</para>
    /// <code>
    /// services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    ///     .AddJwtBearer(options =>
    ///     {
    ///         options.Authority = "https://auth.cocoar.dev/timetodo";
    ///         options.Audience  = "timetodo-api";
    ///         options.GetClaimsFromUserInfoEndpoint = true;
    ///     });
    ///
    /// services.AddCocoarAuthClaimsTransformation(o =>
    /// {
    ///     o.AppSlug = "timetodo";
    /// });
    ///
    /// // Now [Authorize(Roles = "Admin")] just works.
    /// </code>
    /// </summary>
    public static IServiceCollection AddCocoarAuthClaimsTransformation(
        this IServiceCollection services,
        Action<CocoarAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddTransient<IClaimsTransformation, CocoarAuthClaimsTransformation>();
        return services;
    }
}
