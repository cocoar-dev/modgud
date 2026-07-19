using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Marten;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict token-generation handler that switches a client's ACCESS token
/// from the global reference-token default to a self-contained JWT, based on the
/// per-client <see cref="AccessTokenType"/> setting.
///
/// <para><c>UseReferenceAccessTokens()</c> is enabled globally. For a client
/// configured with <see cref="AccessTokenType.Jwt"/>, this handler flips OFF —
/// for the access token of this request only — the two per-request switches
/// OpenIddict reads at generation time (<c>IsReferenceToken</c> and
/// <c>PersistTokenPayload</c>), so a self-contained JWT is emitted instead of an
/// opaque reference identifier.</para>
///
/// <para>It hooks <see cref="GenerateTokenContext"/> — a fresh, per-request
/// context — and MUST NOT mutate <c>context.Options</c>. That options object is
/// the process-wide <see cref="OpenIddictServerOptions"/> singleton
/// (<c>IOptionsMonitor.CurrentValue</c> hands every request the same instance);
/// writing <c>Options.UseReferenceAccessTokens</c> there leaks across requests
/// and races concurrent ones — a single JWT client's sign-in would flip the
/// global default off for every later reference client, silently downgrading
/// their opaque tokens to self-contained JWTs and losing instant revocation.</para>
///
/// <para>Only access tokens are touched; refresh (and any other) token types keep
/// the reference semantics they derive from the global options.</para>
/// </summary>
public sealed class AccessTokenTypeHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseScopedHandler<AccessTokenTypeHandler>()
            // Run before OpenIddict persists/attaches the payload (AttachTokenPayload,
            // which reads IsReferenceToken + PersistTokenPayload). CreateTokenEntry is
            // the earliest stable anchor and ignores both flags, so slotting just
            // ahead of it guarantees every downstream handler sees our override.
            .SetOrder(Protection.CreateTokenEntry.Descriptor.Order - 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IQuerySession _querySession;
    private readonly Cimd.CimdClientResolver _cimdResolver;

    public AccessTokenTypeHandler(IQuerySession querySession, Cimd.CimdClientResolver cimdResolver)
    {
        _querySession = querySession;
        _cimdResolver = cimdResolver;
    }

    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Access tokens only — leave refresh/authorization-code/… to the global
        // reference settings.
        if (context.TokenType != OpenIddictConstants.TokenTypeIdentifiers.AccessToken) return;

        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var app = await _querySession.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted, context.CancellationToken);

        // CIMD clients are non-persisted, so the direct query misses them —
        // fall back to the resolver (cache-warm after the store's resolve
        // earlier this request). CIMD clients always use JWT access tokens.
        app ??= await _cimdResolver.ResolveAsync(clientId, context.CancellationToken);

        if (app is null || app.AccessTokenType != AccessTokenType.Jwt) return;

        // JWT client: emit a self-contained token — don't persist the payload as a
        // reference and don't hand back a reference identifier. The token ENTRY is
        // still created for revocation tracking (governed by CreateTokenEntry /
        // DisableTokenStorage, which we leave untouched).
        context.IsReferenceToken = false;
        context.PersistTokenPayload = false;
    }
}
