using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.Observability;
using Marten;
using Microsoft.Extensions.Logging;
using OpenIddict.Server;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Emits <c>modgud.token.minted.total</c> when the token endpoint
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

    private readonly ITokenMintClientTypeResolver _clientTypeResolver;
    private readonly ILogger<TokenMintMetricHandler> _logger;

    public TokenMintMetricHandler(
        ITokenMintClientTypeResolver clientTypeResolver,
        ILogger<TokenMintMetricHandler> logger)
    {
        _clientTypeResolver = clientTypeResolver;
        _logger = logger;
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
            ModgudMeters.RecordRefreshRejected();
            return;
        }

        // Success path — count the mint.
        if (!string.IsNullOrEmpty(context.Response.Error) ||
            string.IsNullOrEmpty(context.Response.AccessToken))
            return;

        try
        {
            // This handler runs after a rolling refresh token has already been
            // marked Redeemed. Observability is therefore strictly fail-open:
            // use an independent token and swallow classifier/metric failures so
            // request cancellation or a transient Marten/CIMD problem can never
            // turn an already-committed token rotation into a lost 500 response.
            var clientType = await _clientTypeResolver.ResolveAsync(
                context.Request?.ClientId, CancellationToken.None);
            ModgudMeters.RecordTokenMinted(grantType, clientType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and
                                   not StackOverflowException and
                                   not AccessViolationException)
        {
            _logger.LogWarning(ex,
                "Token-mint metric classification failed; the token response will still be returned");
        }
    }
}

/// <summary>
/// Resolves the bounded client-type tag used by <see cref="TokenMintMetricHandler"/>.
/// Kept behind a narrow seam so failure behaviour in the late token-response
/// pipeline can be tested without replacing Marten itself.
/// </summary>
public interface ITokenMintClientTypeResolver
{
    ValueTask<string> ResolveAsync(string? clientId, CancellationToken cancellationToken);
}

internal sealed class TokenMintClientTypeResolver : ITokenMintClientTypeResolver
{
    private readonly IQuerySession _querySession;
    private readonly Cimd.CimdClientResolver _cimdResolver;

    public TokenMintClientTypeResolver(IQuerySession querySession, Cimd.CimdClientResolver cimdResolver)
    {
        _querySession = querySession;
        _cimdResolver = cimdResolver;
    }

    public async ValueTask<string> ResolveAsync(string? clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clientId)) return ModgudMeters.ClientType.Public;

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted, cancellationToken);

        // CIMD clients are non-persisted — a CIMD client_id misses the query
        // and resolves via the resolver; tag it distinctly.
        if (app is null && Cimd.CimdClientId.IsCimdClientId(clientId)
            && await _cimdResolver.ResolveAsync(clientId, cancellationToken) is not null)
        {
            return ModgudMeters.ClientType.Cimd;
        }

        if (app is null) return ModgudMeters.ClientType.Public;

        // CIMD-resolved apps are marked explicitly (they also carry the DCR
        // marker for containment, so check CIMD first).
        if (app.Properties.ContainsKey(OAuthApplicationPropertyKeys.CimdIsResolvedClient))
            return ModgudMeters.ClientType.Cimd;

        // DCR-registered apps carry a DcrRegisteredAt marker in Properties.
        if (app.Properties.ContainsKey(OAuthApplicationPropertyKeys.DcrRegisteredAt))
            return ModgudMeters.ClientType.Dcr;

        return string.Equals(app.ClientType, OAuthClientTypes.Confidential, StringComparison.OrdinalIgnoreCase)
            ? ModgudMeters.ClientType.Confidential
            : ModgudMeters.ClientType.Public;
    }
}
