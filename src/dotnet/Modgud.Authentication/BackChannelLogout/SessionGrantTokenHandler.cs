using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Domain.OAuth.Applications;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>
/// ADR 0009 — records "this client holds tokens of this session" whenever an access
/// token is generated for a principal that carries a <c>sid</c>. Hooks the same
/// <see cref="GenerateTokenContext"/> the realm signing-key handler uses, one step
/// later, so it reads the exact <c>iss</c> that goes into the token (the logout token
/// must repeat it). Client-credentials tokens carry no <c>sid</c> and are skipped.
/// </summary>
public sealed class SessionGrantTokenHandler : IOpenIddictServerHandler<GenerateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
            .UseScopedHandler<SessionGrantTokenHandler>()
            // RealmSigningKeyHandler runs at AttachSecurityCredentials + 100 and stamps
            // the per-realm issuer onto the principal; this one reads it.
            .SetOrder(Protection.AttachSecurityCredentials.Descriptor.Order + 110)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly ISessionGrantService _grants;
    private readonly IQuerySession _query;
    private readonly ILogger<SessionGrantTokenHandler> _logger;

    public SessionGrantTokenHandler(
        ISessionGrantService grants,
        IQuerySession query,
        ILogger<SessionGrantTokenHandler> logger)
    {
        _grants = grants;
        _query = query;
        _logger = logger;
    }

    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TokenType is not "urn:ietf:params:oauth:token-type:access_token")
            return;

        var principal = context.Principal;
        if (principal is null) return;

        var sid = principal.GetClaim(SessionClaimTypes.Sid);
        var subject = principal.GetClaim(OpenIddictConstants.Claims.Subject);
        var clientId = context.Request?.ClientId ?? principal.GetPresenters().FirstOrDefault();
        if (!Guid.TryParse(sid, out var sessionId) || !Guid.TryParse(subject, out var userId) || string.IsNullOrEmpty(clientId))
            return;

        var issuer = principal.GetClaim(OpenIddictConstants.Claims.Private.Issuer)
                     ?? context.Options.Issuer?.AbsoluteUri
                     ?? context.BaseUri?.AbsoluteUri;
        if (string.IsNullOrEmpty(issuer))
        {
            _logger.LogWarning("Session grant for client {ClientId} skipped: no issuer resolved", clientId);
            return;
        }

        // Native sessions carry their ClientSession id as sid; everything else is a browser session.
        var kind = string.Equals(principal.GetClaim(SessionClaimTypes.ClientSessionId), sid, StringComparison.Ordinal)
            ? AccessSessionKind.Native
            : AccessSessionKind.Browser;

        // CIMD clients are synthesized (no document) — they still get a grant, without an application id.
        var applicationId = await _query.Query<OAuthApplicationState>()
            .Where(a => a.ClientId == clientId && !a.IsDeleted)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(CancellationToken.None);

        // Not fail-open: a missing grant is a relying party that never learns about the
        // logout. A storage failure fails the token response like any other write would.
        await _grants.RecordIssuanceAsync(
            sessionId,
            userId,
            clientId,
            applicationId == Guid.Empty ? string.Empty : applicationId.ToString(),
            kind,
            issuer,
            CancellationToken.None);
    }
}
