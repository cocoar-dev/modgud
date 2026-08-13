using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Audit;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Issue #124 — records a <c>security.refresh_token_reuse_detected</c> audit
/// event (+ a Warning log line) the moment OpenIddict is about to reject a
/// redeemed refresh token re-presented at <c>/connect/token</c> — the RFC
/// 6749 §10.4 compromise signal. Until this handler, that teardown was
/// entirely silent: no log, no audit trail.
///
/// <para><b>Why here and not in <c>AuthorizationEndpoints.ExchangeAsync</c>.</b>
/// <c>OpenIddictExtensions</c> configures a short refresh-token reuse leeway.
/// Outside that window OpenIddict's OWN stock
/// <see cref="Protection.ValidateTokenEntry"/> handler — part of the ASP.NET
/// Core authentication-middleware pass that runs BEFORE routing reaches our
/// minimal-API endpoint — rejects the request and revokes the whole token
/// family itself, short-circuiting before the minimal-API handler ever runs.
/// (An earlier <c>DetectRefreshTokenReuseAsync</c> helper lived in
/// <c>ExchangeAsync</c> to tear down the chain from there; it was dead code
/// for the same reason and was removed — issue #130.) Hooking the stock
/// pipeline directly, instead, is guaranteed to run for every reuse rejection
/// regardless of grant path.</para>
///
/// <para><b>Precision over <see cref="TokenMintMetricHandler"/>.</b> That
/// handler's refresh-rejection metric is an explicitly-documented "high-signal
/// proxy" — any <c>invalid_grant</c> on a refresh_token request counts,
/// including ordinary expiry/revocation, not just reuse. This handler instead
/// runs BEFORE <see cref="Protection.ValidateTokenEntry"/> and checks the
/// token's OWN status is exactly <c>Redeemed</c> — the one status that can
/// only mean "the legitimate holder already rotated past this token", so the
/// audit row is reuse-specific, not noisy.</para>
/// </summary>
public sealed class RefreshTokenReuseAuditHandler
    : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RefreshTokenReuseAuditHandler>()
            // Run immediately BEFORE the stock handler that rejects + revokes
            // the token family on redeemed-token reuse, so we observe the
            // "still Redeemed, not yet torn down" state and can attribute the
            // event (subject/client/authorization/family size) before the
            // teardown fires. Order - 1, not + 100: unlike the usual
            // "override a value the stock handler already set" case, this is
            // an insert-before-observation, mirroring RealmTokenValidationHandler.
            .SetOrder(Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly ISecurityAuditLog _securityAudit;
    private readonly IEnumerable<IRefreshTokenReuseObserver> _observers;
    private readonly ILogger<RefreshTokenReuseAuditHandler> _logger;

    public RefreshTokenReuseAuditHandler(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictApplicationManager applicationManager,
        ISecurityAuditLog securityAudit,
        IEnumerable<IRefreshTokenReuseObserver> observers,
        ILogger<RefreshTokenReuseAuditHandler> logger)
    {
        _tokenManager = tokenManager;
        _applicationManager = applicationManager;
        _securityAudit = securityAudit;
        _observers = observers;
        _logger = logger;
    }

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only refresh tokens carry redeemed-reuse semantics — an already-used
        // authorization code or a revoked access/id token is a different
        // (already-audited-elsewhere, or simply expected) situation.
        // ValidTokenTypes carries the RFC 8693 type-identifier URI form
        // (TokenTypeIdentifiers), not the short TokenTypeHints form — the
        // hint constant never matches here.
        if (!context.ValidTokenTypes.Contains(TokenTypeIdentifiers.RefreshToken)) return;

        // RestoreTokenEntryProperties (which runs earlier in this same
        // pipeline) populates TokenId from the store entry when one exists.
        // Fall back to a direct reference-id lookup in case that didn't fire
        // for some reason — defensive, not required by observed behaviour.
        var token = !string.IsNullOrEmpty(context.TokenId)
            ? await _tokenManager.FindByIdAsync(context.TokenId)
            : (!string.IsNullOrEmpty(context.Token) ? await _tokenManager.FindByReferenceIdAsync(context.Token) : null);
        if (token is null) return;

        var status = await _tokenManager.GetStatusAsync(token);
        if (!string.Equals(status, Statuses.Redeemed, StringComparison.OrdinalIgnoreCase)) return;

        // Mirror OpenIddict 7.6 Protection.ValidateTokenEntry.IsReusableAsync
        // exactly. This observer runs immediately before the stock handler, so
        // looking only at Status=Redeemed would misclassify a permitted retry
        // inside the response-loss/concurrency window as an attack, emit a false
        // audit event, and delete the ClientSession even though OpenIddict itself
        // is about to accept the token.
        if (context.Options.RefreshTokenReuseLeeway is { } leeway)
        {
            var redemptionDate = await _tokenManager.GetRedemptionDateAsync(
                token, context.CancellationToken);
            if (redemptionDate is null ||
                context.Options.TimeProvider.GetUtcNow() < redemptionDate.Value + leeway)
            {
                return;
            }
        }

        var authorizationId = await _tokenManager.GetAuthorizationIdAsync(token) ?? context.AuthorizationId;
        var subject = context.Principal?.GetClaim(Claims.Subject) ?? await _tokenManager.GetSubjectAsync(token);

        string? clientId = null;
        var applicationId = await _tokenManager.GetApplicationIdAsync(token);
        if (!string.IsNullOrEmpty(applicationId))
        {
            var application = await _applicationManager.FindByIdAsync(applicationId);
            if (application is not null)
                clientId = await _applicationManager.GetClientIdAsync(application);
        }

        // The family size at THIS instant — the stock handler that runs right
        // after this one revokes every token tied to the authorization as its
        // own reuse response. Count now (before that teardown) so the audit
        // row carries the blast radius without this handler duplicating the
        // revoke itself.
        var familySize = 0;
        if (!string.IsNullOrEmpty(authorizationId))
        {
            await foreach (var _ in _tokenManager.FindByAuthorizationIdAsync(authorizationId))
                familySize++;
        }

        _logger.LogWarning(
            "Refresh token reuse detected for user {UserId}, client {ClientId}, authorization {AuthorizationId} — {FamilySize} token(s) about to be revoked",
            subject, clientId, authorizationId, familySize);

        await _securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RefreshTokenReuseDetected,
            Severity = AuditSeverity.Warning,
            ActorKind = AuditActorKind.User,
            ActorSubjectId = Guid.TryParse(subject, out var subjectId) ? subjectId : null,
            OAuthClientId = clientId,
            AuthorizationId = authorizationId,
            OutcomeCode = AuditOutcomes.Blocked,
            OperationCode = "revoke-token-family",
            Count = familySize,
        }, context.CancellationToken);

        // Keep higher-level session models in sync with OpenIddict's imminent
        // token-family teardown. Observer failures must never interrupt the
        // stock security response that runs immediately after this handler.
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnReuseDetectedAsync(
                    subject, clientId, authorizationId, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "refresh-token reuse observer {ObserverType} failed for authorization {AuthorizationId}",
                    observer.GetType().Name,
                    authorizationId);
            }
        }
    }
}
