using Microsoft.AspNetCore.Http;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// RFC 9207 (OAuth 2.0 Authorization Server Issuer Identification): the
/// authorization response carries an <c>iss</c> parameter that strict clients —
/// increasingly including MCP clients (claude.ai, ChatGPT) — compare, via simple
/// string comparison, against the <c>issuer</c> from discovery.
///
/// <para>Modgud derives the real issuer per-realm from the request host, but
/// configures OpenIddict with a fixed placeholder <c>Options.Issuer</c>
/// (<c>https://issuer.invalid/</c>). OpenIddict's stock <c>AttachIssuer</c> emits
/// <c>context.Issuer = Options.Issuer ?? BaseUri</c> — i.e. the placeholder — into
/// the authorization response. Discovery already serves the realm host (see
/// <see cref="RealmIssuerHandler"/>) and the token <c>iss</c> claim is stamped
/// per-realm (see <c>RealmSigningKeyHandler</c>); this handler is the missing third
/// site. It runs after the stock handler and overwrites the emitted <c>iss</c> with
/// <c>BaseUri.AbsoluteUri</c>, which is exactly how the discovery <c>issuer</c> is
/// serialized — so a client's <c>iss</c>-vs-discovery comparison passes.</para>
///
/// <para>Without this, strict RFC 9207 clients reject the redirect with an
/// issuer mismatch — a hard-to-debug interop failure (discovery looks fine, tokens
/// validate, but the authorize step fails only for clients that check <c>iss</c>).</para>
/// </summary>
public sealed class RealmAuthorizationResponseIssuerHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyAuthorizationResponseContext>()
            // Run AFTER the stock AttachIssuer attached the (placeholder) iss, then
            // correct the value — same "observe-then-override" ordering as
            // RealmIssuerHandler does for the discovery document.
            .SetOrder(Authentication.AttachIssuer.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .UseSingletonHandler<RealmAuthorizationResponseIssuerHandler>()
            .Build();

    private readonly IHttpContextAccessor _httpContextAccessor;

    public RealmAuthorizationResponseIssuerHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only correct a value the stock pipeline actually emitted, and only when
        // we have a request-derived issuer — never invent or add an iss the server
        // didn't intend to send. ADR-0011: anchor to the tenant canonical origin
        // when on an Application subdomain (else the request host, as before).
        if (context.BaseUri is { IsAbsoluteUri: true } baseUri &&
            !string.IsNullOrEmpty((string?)context.Response[Parameters.Iss]))
        {
            var issuer = CanonicalIssuer.Resolve(baseUri, _httpContextAccessor.HttpContext);
            if (issuer is not null) context.Response[Parameters.Iss] = issuer.AbsoluteUri;
        }

        return default;
    }
}
