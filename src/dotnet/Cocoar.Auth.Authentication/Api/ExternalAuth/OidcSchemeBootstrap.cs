using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

/// <summary>
/// On application start, loads every enabled, non-deleted login provider from
/// every active realm and registers the corresponding OIDC scheme. Runs AFTER
/// Marten's startup schema check so the documents can be queried safely.
///
/// <para>
/// WOLV-02 fix: previously this hosted service only saw the system realm's
/// login providers because <c>IQuerySession</c> resolved through the
/// <c>TenantedSessionFactory</c> with no <c>HttpContext</c> present —
/// resulting in the "system" tenant fallback. Realms with their own external
/// IdPs configured silently failed at cold start until some other event
/// happened to fire against them. Now we enumerate every active realm and
/// re-enter <see cref="TenantContext"/> per realm so the per-tenant Marten
/// session reads the correct database.
/// </para>
///
/// <para>
/// Event-driven re-registration for runtime config changes lives in
/// <c>LoginProviderEventHandlers</c> — this service handles the cold-start only.
/// </para>
///
/// <para>
/// Pre-filters on <c>Type == LoginProviderType.Oidc</c> so non-Oidc providers
/// (Internal, plus the not-yet-wired Saml/Ldap/Kerberos) never enter the
/// scheme-registration path. <see cref="DynamicOidcSchemeManager.RegisterAsync"/>
/// double-checks defensively; the bootstrap pre-filter just keeps logs clean.
/// </para>
/// </summary>
public class OidcSchemeBootstrap(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache,
    ILogger<OidcSchemeBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var realms = await realmCache.GetAllActiveAsync();
        var totalRegistered = 0;

        foreach (var realm in realms)
        {
            // TenantContext.Enter sets the AsyncLocal that TenantedSessionFactory
            // reads when no HttpContext is present — without it the session
            // would query the system tenant for every realm and miss everything
            // outside it.
            using var _ = TenantContext.Enter(realm.Slug);
            using var scope = scopeFactory.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();

            var enabled = await session.Query<LoginProvider>()
                .Where(c => !c.IsDeleted && c.Enabled && c.Type == LoginProviderType.Oidc)
                .ToListAsync(cancellationToken);

            foreach (var config in enabled)
            {
                try { await manager.RegisterAsync(config); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Auth: Bootstrap registration failed for LoginProvider {Id} in realm {Realm}", config.Id, realm.Slug);
                }
            }

            totalRegistered += enabled.Count;
            if (enabled.Count > 0)
                logger.LogDebug("Auth: OidcSchemeBootstrap registered {Count} schemes in realm {Realm}", enabled.Count, realm.Slug);
        }

        logger.LogInformation("Auth: OidcSchemeBootstrap registered {Count} external auth schemes across {Realms} realm(s)",
            totalRegistered, realms.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
