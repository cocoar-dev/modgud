using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Modgud.Client.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires the Modgud resource-server integration into a host:
    /// <list type="bullet">
    ///   <item>A <c>JwtBearerEvents.OnTokenValidated</c> handler that
    ///   fetches <c>{Authority}/connect/userinfo</c> with the user's
    ///   bearer token and merges the <c>resource_access</c> claim onto
    ///   the principal.</item>
    ///   <item>The pre-request <see cref="ModgudClaimsTransformation"/>
    ///   that flattens <c>resource_access[<see cref="ModgudOptions.Audience"/>]</c>
    ///   into native <see cref="System.Security.Claims.ClaimTypes.Role"/>,
    ///   <c>"permission"</c> and <c>"group"</c> claims.</item>
    ///   <item>The <see cref="RequiresCocoarPermissionFilter"/> endpoint
    ///   filter (consumed via the <c>RequiresCocoarPermission</c>
    ///   extension).</item>
    /// </list>
    ///
    /// <para>The IdP pre-expands bypass tiers (<c>realm:admin</c>,
    /// <c>&lt;r&gt;:admin</c>) before emission, so the lib doesn't need
    /// any evaluator logic — exact-match against the <c>"permission"</c>
    /// claims is sufficient.</para>
    ///
    /// <para>Typical usage in a resource-server <c>Program.cs</c>:</para>
    /// <code>
    /// services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    ///     .AddJwtBearer(options =>
    ///     {
    ///         options.Authority = "https://auth.example.com";
    ///         options.Audience  = "https://policy-api.cocoar.dev";
    ///     });
    ///
    /// services.AddModgudClient(o =>
    /// {
    ///     o.Authority = "https://auth.example.com";
    ///     o.Audience  = "https://policy-api.cocoar.dev";
    /// });
    ///
    /// // Now [Authorize(Roles = "Editor")] and
    /// // .RequiresCocoarPermission("policy:write") just work.
    /// </code>
    /// </summary>
    public static IServiceCollection AddModgudClient(
        this IServiceCollection services,
        Action<ModgudOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddTransient<IClaimsTransformation, ModgudClaimsTransformation>();

        // Hook UserInfo-fetching into the JwtBearer scheme — pure
        // AddJwtBearer doesn't do that natively. Composable: any
        // existing OnTokenValidated handler is preserved.
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ModgudJwtBearerPostConfigure>();

        return services;
    }
}
