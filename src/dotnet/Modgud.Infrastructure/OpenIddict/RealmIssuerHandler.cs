using OpenIddict.Server;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict event handler that overrides the issuer in the discovery document
/// with the request's BaseUri (which already includes PathBase, set by OpenIddict's
/// ResolveRequestUri handler). This makes /.well-known/openid-configuration
/// realm-aware: each realm gets its own issuer when accessed via its own host.
/// </summary>
public class RealmIssuerHandler : IOpenIddictServerHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.HandleConfigurationRequestContext>()
            .UseSingletonHandler<RealmIssuerHandler>()
            .SetOrder(OpenIddictServerHandlers.Discovery.AttachIssuer.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.BuiltIn)
            .Build();

    public ValueTask HandleAsync(OpenIddictServerEvents.HandleConfigurationRequestContext context)
    {
        if (context.BaseUri is not null)
        {
            context.Issuer = context.BaseUri;
        }
        return default;
    }
}
