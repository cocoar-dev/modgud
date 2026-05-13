using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Infrastructure.Observability;
using Marten;
using OpenIddict.Server;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Emits <c>cocoar_auth.token.minted.total</c> when the token endpoint
/// successfully issues an access token. Tags: realm, grant_type, client_type.
///
/// <para>Hooks <see cref="OpenIddictServerEvents.ApplyTokenResponseContext"/>
/// (fires just before the JSON response is written) and bails when no
/// access token is in the response — that's the error path.</para>
/// </summary>
public sealed class TokenMintMetricHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyTokenResponseContext>()
            .UseScopedHandler<TokenMintMetricHandler>()
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IQuerySession _querySession;

    public TokenMintMetricHandler(IQuerySession querySession)
    {
        _querySession = querySession;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        var grantType = context.Request?.GrantType ?? "unknown";

        // Refresh-token rejection — high-signal proxy for reuse-detection,
        // see the meter's description for the noise it also captures.
        if (!string.IsNullOrEmpty(context.Response.Error)
            && string.Equals(grantType, "refresh_token", StringComparison.Ordinal)
            && string.Equals(context.Response.Error, "invalid_grant", StringComparison.Ordinal))
        {
            CocoarAuthMeters.RecordRefreshRejected();
            return;
        }

        // Success path — count the mint.
        if (!string.IsNullOrEmpty(context.Response.Error) ||
            string.IsNullOrEmpty(context.Response.AccessToken))
            return;

        var clientType = await ResolveClientTypeAsync(context.Request?.ClientId);
        CocoarAuthMeters.RecordTokenMinted(grantType, clientType);
    }

    private async ValueTask<string> ResolveClientTypeAsync(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return CocoarAuthMeters.ClientType.Public;

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted);

        if (app is null) return CocoarAuthMeters.ClientType.Public;

        // DCR-registered apps carry a DcrRegisteredAt marker in Properties.
        if (app.Properties.ContainsKey(OAuthApplicationPropertyKeys.DcrRegisteredAt))
            return CocoarAuthMeters.ClientType.Dcr;

        return string.Equals(app.ClientType, OAuthClientTypes.Confidential, StringComparison.OrdinalIgnoreCase)
            ? CocoarAuthMeters.ClientType.Confidential
            : CocoarAuthMeters.ClientType.Public;
    }
}
