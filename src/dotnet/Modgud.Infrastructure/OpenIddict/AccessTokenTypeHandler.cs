using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Marten;
using OpenIddict.Server;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict server event handler that switches access tokens from reference to JWT
/// based on the per-client <see cref="AccessTokenType"/> setting.
///
/// <para>By default, <c>UseReferenceAccessTokens()</c> is enabled globally. For clients
/// configured with <see cref="AccessTokenType.Jwt"/>, this handler disables reference
/// token storage for that request so OpenIddict generates a self-contained JWT instead.</para>
/// </summary>
public sealed class AccessTokenTypeHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseScopedHandler<AccessTokenTypeHandler>()
            .SetOrder(OpenIddictServerHandlers.GenerateAccessToken.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IQuerySession _querySession;
    private readonly Cimd.CimdClientResolver _cimdResolver;

    public AccessTokenTypeHandler(IQuerySession querySession, Cimd.CimdClientResolver cimdResolver)
    {
        _querySession = querySession;
        _cimdResolver = cimdResolver;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted);

        // CIMD clients are non-persisted, so the direct query misses them —
        // fall back to the resolver (cache-warm after the store's resolve
        // earlier this request). CIMD clients always use JWT access tokens.
        app ??= await _cimdResolver.ResolveAsync(clientId, context.CancellationToken);

        if (app is null || app.AccessTokenType != AccessTokenType.Jwt) return;

        // For JWT clients: disable reference token storage for this request.
        context.Options.UseReferenceAccessTokens = false;
    }
}
