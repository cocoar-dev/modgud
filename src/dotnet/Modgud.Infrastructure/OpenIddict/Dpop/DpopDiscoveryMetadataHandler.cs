using System.Collections.Immutable;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Advertises <c>dpop_signing_alg_values_supported</c> in
/// <c>/.well-known/openid-configuration</c> (RFC 9449 §5.1) so clients and
/// resource servers can discover that this AS speaks DPoP and which proof
/// signing algorithms it accepts. The list is sourced from
/// <see cref="DpopProofValidator.SupportedSigningAlgorithms"/> — the same set the
/// token endpoint enforces — so what's advertised can't drift from what's
/// accepted.
///
/// <para>Unconditional: DPoP is offered to every realm (it's opt-in per request,
/// never mandated globally), so unlike the CIMD/DCR metadata handlers there's no
/// per-realm gate and no database read. Mirrors their use of the free-form
/// <c>HandleConfigurationRequestContext.Metadata</c> dict, which OpenIddict has
/// no typed property for this field.</para>
/// </summary>
public sealed class DpopDiscoveryMetadataHandler : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseSingletonHandler<DpopDiscoveryMetadataHandler>()
            // After the stock endpoint-attach handlers, same convention as the
            // CIMD / DCR discovery-metadata handlers.
            .SetOrder(Discovery.AttachEndpoints.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Metadata[DpopConstants.SigningAlgValuesMetadataKey] =
            new OpenIddictParameter(DpopProofValidator.SupportedSigningAlgorithms.ToImmutableArray());
        return default;
    }
}
