using Microsoft.AspNetCore.Http;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Sets the token response's <c>token_type</c> to <c>DPoP</c> instead of
/// <c>Bearer</c> when the access token was bound to a DPoP proof key earlier in
/// the request (RFC 9449 §5). The value tells the client it must present the
/// token with the <c>Authorization: DPoP</c> scheme plus a fresh proof, rather
/// than as a plain bearer token.
///
/// <para>
/// Hooks <see cref="ProcessSignInContext"/> right AFTER OpenIddict's
/// <c>AttachSignInParameters</c> handler — that's where the default
/// <c>token_type=Bearer</c> is written onto the response — so our override lands
/// on the same response object and isn't clobbered. (Overriding later, at
/// <c>ApplyTokenResponse</c> time, races OpenIddict's terminal JSON writer.)
/// Only rewrites the type when an access token was actually issued.
/// </para>
/// </summary>
public sealed class DpopTokenTypeHandler : IOpenIddictServerHandler<ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessSignInContext>()
            .UseScopedHandler<DpopTokenTypeHandler>()
            // Immediately after the default token_type=Bearer is attached.
            .SetOrder(AttachSignInParameters.Descriptor.Order + 10)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public DpopTokenTypeHandler(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var items = _httpContextAccessor.HttpContext?.Items;
        if (context.Response.AccessToken is { Length: > 0 } &&
            items is not null &&
            items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) &&
            raw is string jkt && jkt.Length > 0)
        {
            context.Response.TokenType = DpopConstants.TokenType;
        }

        return default;
    }
}
