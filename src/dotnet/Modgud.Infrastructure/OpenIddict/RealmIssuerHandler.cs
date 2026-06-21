using Microsoft.AspNetCore.Http;
using OpenIddict.Server;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict event handler that overrides the issuer in the discovery document
/// with the request's BaseUri (which already includes PathBase, set by OpenIddict's
/// ResolveRequestUri handler). This makes /.well-known/openid-configuration
/// realm-aware: each realm gets its own issuer when accessed via its own host.
///
/// <para>ADR-0011: when the request arrives on an Application subdomain, the
/// issuer is anchored to the tenant canonical origin (see <see cref="CanonicalIssuer"/>)
/// — an Application subdomain must not advertise itself as its own issuer. Plain
/// realm domains are unchanged.</para>
/// </summary>
public class RealmIssuerHandler : IOpenIddictServerHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.HandleConfigurationRequestContext>()
            .UseSingletonHandler<RealmIssuerHandler>()
            .SetOrder(OpenIddictServerHandlers.Discovery.AttachIssuer.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.BuiltIn)
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public RealmIssuerHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ValueTask HandleAsync(OpenIddictServerEvents.HandleConfigurationRequestContext context)
    {
        if (context.BaseUri is not null)
        {
            context.Issuer = CanonicalIssuer.Resolve(context.BaseUri, _httpContextAccessor.HttpContext);
        }
        return default;
    }
}
