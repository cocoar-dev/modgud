using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

/// <summary>
/// On application start, loads every enabled, non-deleted login provider from
/// the database and registers the corresponding OIDC scheme. Runs AFTER
/// Marten's startup schema check so the documents can be queried safely.
/// <para>
/// Event-driven re-registration for runtime config changes lives in
/// <c>LoginProviderEventHandlers</c> — this service handles the cold-start only.
/// </para>
/// <para>
/// Phase 1 note: Internal-typed providers do not have a flavor and are filtered
/// out of <see cref="DynamicOidcSchemeManager.RegisterAsync"/> via the unknown-
/// flavor early-return. Phase 2 will add an explicit
/// <c>Type == LoginProviderType.Oidc</c> filter at the source.
/// </para>
/// </summary>
public class OidcSchemeBootstrap(
    IServiceScopeFactory scopeFactory,
    ILogger<OidcSchemeBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();

        var enabled = await session.Query<LoginProvider>()
            .Where(c => !c.IsDeleted && c.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var config in enabled)
        {
            try { await manager.RegisterAsync(config); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auth: Bootstrap registration failed for LoginProvider {Id}", config.Id);
            }
        }

        logger.LogInformation("Auth: OidcSchemeBootstrap registered {Count} external auth schemes", enabled.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
