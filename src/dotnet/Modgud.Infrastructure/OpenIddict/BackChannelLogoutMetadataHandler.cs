using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// ADR 0021 — advertises OpenID Connect Back-Channel Logout 1.0 support in the
/// discovery document (<c>backchannel_logout_supported</c>,
/// <c>backchannel_logout_session_supported</c>). Realm-independent: every realm can
/// register logout URIs on its clients and every logout token carries <c>sid</c> when
/// the ended scope names a session.
/// </summary>
public sealed class BackChannelLogoutMetadataHandler : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseSingletonHandler<BackChannelLogoutMetadataHandler>()
            .SetOrder(Discovery.AttachEndpoints.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Metadata["backchannel_logout_supported"] = true;
        context.Metadata["backchannel_logout_session_supported"] = true;
        return default;
    }
}
