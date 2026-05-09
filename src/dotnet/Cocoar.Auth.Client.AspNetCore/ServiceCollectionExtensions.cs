using Cocoar.Auth.Client.AspNetCore.Distribution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Client.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires the Cocoar.Auth resource-server integration into a host:
    /// typed distribution-API client, in-memory permissions cache,
    /// pre-request claims-transformation, and the
    /// <c>RequiresCocoarPermission</c> endpoint filter.
    ///
    /// <para>Typical usage in a resource-server <c>Program.cs</c>:</para>
    /// <code>
    /// services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    ///     .AddJwtBearer(options =>
    ///     {
    ///         options.Authority = "https://auth.cocoar.dev";
    ///         options.Audience  = "policy-api";
    ///     });
    ///
    /// services.AddCocoarAuthClient(o =>
    /// {
    ///     o.AppSlug              = "cocoar-policy";
    ///     o.IdpBaseUrl           = "https://auth.cocoar.dev";
    ///     o.ResourceServerId     = "policy-api";
    ///     o.ResourceServerSecret = builder.Configuration["Cocoar:RSSecret"]!;
    /// });
    ///
    /// // Now [Authorize(Roles = "Editor")] and
    /// // .RequiresCocoarPermission("policy:write") just work.
    /// </code>
    /// </summary>
    public static IServiceCollection AddCocoarAuthClient(
        this IServiceCollection services,
        Action<CocoarAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        // Need IHttpContextAccessor for forwarding the user-bearer-token
        // out of the incoming request. Idempotent registration — host
        // may already have it.
        services.AddHttpContextAccessor();

        // 30-second permission cache backing the claims-transformation.
        // Idempotent in case the host already added MemoryCache.
        services.AddMemoryCache();
        services.AddSingleton<PermissionsCache>();

        // Typed HttpClient → Distribution-API. BaseAddress is patched in
        // a lambda so trailing-slash mistakes in the configured URL are
        // tolerated. Kept transient so the underlying SocketsHttpHandler
        // pool-rotation logic does its job.
        services.AddHttpClient<IDistributionClient, DistributionClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CocoarAuthOptions>>().Value;
            var baseUrl = options.IdpBaseUrl.TrimEnd('/') + '/';
            http.BaseAddress = new Uri(baseUrl);
        });

        services.AddTransient<IClaimsTransformation, CocoarAuthClaimsTransformation>();

        return services;
    }
}
