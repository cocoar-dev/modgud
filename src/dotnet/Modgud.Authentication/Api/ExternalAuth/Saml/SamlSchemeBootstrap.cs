using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Cold-start counterpart of <see cref="OidcSchemeBootstrap"/> for SAML. On
/// application start, walks every active realm and registers every enabled
/// SAML <c>LoginProvider</c> with <see cref="DynamicSamlSchemeManager"/>.
/// <para>
/// Without this, the cache would only be populated by runtime events (admin
/// edits, provider toggles) — so a freshly-started server would miss every
/// SAML provider that exists in a realm until *something* poked the
/// LoginProvider record into the event stream. Same fix as WOLV-02 on the
/// OIDC side: re-enter <see cref="TenantContext"/> per realm so the
/// per-tenant Marten session reads the correct database.
/// </para>
/// </summary>
public class SamlSchemeBootstrap(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache,
    ILogger<SamlSchemeBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var realms = await realmCache.GetAllActiveAsync();
        var totalRegistered = 0;

        foreach (var realm in realms)
        {
            using var _ = TenantContext.Enter(realm.Slug);
            using var scope = scopeFactory.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var manager = scope.ServiceProvider.GetRequiredService<DynamicSamlSchemeManager>();

            var enabled = await session.Query<LoginProvider>()
                .Where(c => !c.IsDeleted && c.Enabled && c.Type == LoginProviderType.Saml)
                .ToListAsync(cancellationToken);

            foreach (var config in enabled)
            {
                try { await manager.RegisterAsync(config); }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Auth: SAML bootstrap registration failed for LoginProvider {Id} in realm {Realm}",
                        config.Id, realm.Slug);
                }
            }

            totalRegistered += enabled.Count;
            if (enabled.Count > 0)
            {
                logger.LogDebug(
                    "Auth: SamlSchemeBootstrap registered {Count} providers in realm {Realm}",
                    enabled.Count, realm.Slug);
            }
        }

        logger.LogInformation(
            "Auth: SamlSchemeBootstrap registered {Count} SAML providers across {Realms} realm(s)",
            totalRegistered, realms.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
