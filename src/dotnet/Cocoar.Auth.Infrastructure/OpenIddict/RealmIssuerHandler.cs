using Microsoft.AspNetCore;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict event handler that sets the issuer in the discovery document to include the realm PathBase.
/// OpenIddict already uses PathBase for endpoint routing but uses the configured static Issuer URI
/// for the "issuer" claim in discovery and tokens. This handler overrides it with the request's BaseUri
/// which already includes PathBase (set by OpenIddict's ResolveRequestUri handler).
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
		// Use BaseUri (which includes PathBase, set by OpenIddict's ResolveRequestUri)
		// instead of the static configured Issuer
		if (context.BaseUri is not null)
		{
			context.Issuer = context.BaseUri;
		}

		return default;
	}
}
