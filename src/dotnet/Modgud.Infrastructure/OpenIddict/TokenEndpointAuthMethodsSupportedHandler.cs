using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Advertises the public-client authentication method (<c>none</c>) in the
/// discovery document's <c>token_endpoint_auth_methods_supported</c>.
///
/// <para>OpenIddict's stock <see cref="Discovery.AttachClientAuthenticationMethods"/>
/// only emits the methods in <c>Options.ClientAuthenticationMethods</c> — the
/// confidential ones (<c>client_secret_basic</c> / <c>client_secret_post</c> /
/// <c>private_key_jwt</c>) — and never adds <c>none</c>. But Modgud genuinely
/// supports public PKCE clients (admin-created AND self-registered via
/// <c>/connect/register</c>), so without this the metadata omits a method the
/// server accepts. A spec-conformant client that reads the list and picks an
/// advertised method (e.g. the claude.ai MCP connector) should see every method
/// it can actually use. We only append, never remove, so the confidential
/// methods the stock handler emitted stay intact.</para>
/// </summary>
public sealed class TokenEndpointAuthMethodsSupportedHandler
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    // RFC 8414 token_endpoint_auth_methods_supported value for public clients.
    private const string None = "none";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            // Run AFTER the stock handler populates the confidential methods so
            // we append to its set rather than risk being overwritten.
            .SetOrder(Discovery.AttachClientAuthenticationMethods.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .UseSingletonHandler<TokenEndpointAuthMethodsSupportedHandler>()
            .Build();

    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.TokenEndpointAuthenticationMethods.Add(None);
        return default;
    }
}
