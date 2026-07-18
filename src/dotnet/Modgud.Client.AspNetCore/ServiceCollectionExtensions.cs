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
    ///   <item>The <see cref="RequiresModgudPermissionFilter"/> endpoint
    ///   filter (consumed via the <c>RequiresModgudPermission</c>
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
    /// // .RequiresModgudPermission("policy:write") just work.
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

    /// <summary>
    /// Registers the Modgud <b>reference-token</b> (introspection) authentication
    /// scheme so a resource server can accept Modgud's default opaque access
    /// tokens without switching its OAuth client to JWT. Each request validates
    /// the bearer token via <c>/connect/introspect</c> (RFC 7662) and projects
    /// the response — including the per-audience <c>resource_access</c> block —
    /// onto the principal, where the shared <see cref="ModgudClaimsTransformation"/>
    /// flattens it into role / permission claims. The
    /// <c>RequiresModgudPermission</c> filter then works identically to the JWT
    /// path.
    ///
    /// <para>The resource server introspects with a confidential client whose
    /// <c>client_id</c> equals its own <see cref="ModgudReferenceTokenOptions.Audience"/>
    /// — see the options docs for why. Set the secret via
    /// <see cref="ModgudReferenceTokenOptions.IntrospectionClientSecret"/>.</para>
    ///
    /// <para>Typical usage in a resource-server <c>Program.cs</c>:</para>
    /// <code>
    /// services.AddAuthentication(ModgudReferenceTokenDefaults.AuthenticationScheme)
    ///     .AddModgudReferenceTokenClient(o =>
    ///     {
    ///         o.Authority = "https://auth.example.com";
    ///         o.Audience  = "https://mcp.acme.example";   // == introspection client_id
    ///         o.IntrospectionClientSecret = builder.Configuration["Modgud:IntrospectionSecret"];
    ///     });
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddModgudReferenceTokenClient(
        this AuthenticationBuilder builder,
        Action<ModgudReferenceTokenOptions> configure)
        => builder.AddModgudReferenceTokenClient(
            ModgudReferenceTokenDefaults.AuthenticationScheme, configure);

    /// <summary>
    /// Scheme-named overload of
    /// <see cref="AddModgudReferenceTokenClient(AuthenticationBuilder, Action{ModgudReferenceTokenOptions})"/>,
    /// for hosts that register the introspection handler under a custom scheme
    /// name (e.g. to run it alongside JwtBearer).
    /// </summary>
    public static AuthenticationBuilder AddModgudReferenceTokenClient(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<ModgudReferenceTokenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddTransient<IClaimsTransformation, ModgudClaimsTransformation>();

        // The shared ModgudClaimsTransformation reads ModgudOptions.Audience to
        // pick the resource_access[...] block, so mirror the audience there.
        builder.Services.Configure<ModgudReferenceTokenOptions>(authenticationScheme, configure);
        builder.Services.AddOptions<ModgudOptions>().Configure<IOptionsMonitor<ModgudReferenceTokenOptions>>(
            (modgud, refToken) =>
            {
                var o = refToken.Get(authenticationScheme);
                modgud.Authority = o.Authority;
                modgud.Audience = o.Audience;
            });

        return builder.AddScheme<ModgudReferenceTokenOptions, ModgudIntrospectionHandler>(
            authenticationScheme, configure);
    }
}
