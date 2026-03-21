using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Marten;
using OpenIddict.Server;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict server event handler that switches access tokens from reference to JWT
/// based on the client's AccessTokenType setting.
///
/// By default, UseReferenceAccessTokens() is enabled globally (reference tokens for all clients).
/// For clients configured with AccessTokenType.Jwt, this handler disables reference token storage
/// so OpenIddict generates a self-contained JWT instead.
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

    public AccessTokenTypeHandler(IQuerySession querySession)
    {
        _querySession = querySession;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted);

        if (app is null || app.AccessTokenType != AccessTokenType.Jwt)
        {
            return;
        }

        // For JWT clients: disable reference token storage for this request.
        // OpenIddict will generate a self-contained JWT instead of storing server-side.
        context.Options.UseReferenceAccessTokens = false;
    }
}
