using Marten;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

/// <summary>
/// On application start, loads every enabled, non-deleted IdP config from
/// the database and registers the corresponding OIDC scheme. Runs AFTER
/// Marten's startup schema check so the documents can be queried safely.
/// <para>
/// Event-driven re-registration for runtime config changes lives in
/// <c>IdpConfigEventHandlers</c> — this service handles the cold-start only.
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

        var enabled = await session.Query<IdpConfig>()
            .Where(c => !c.IsDeleted && c.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var config in enabled)
        {
            try { await manager.RegisterAsync(config); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auth: Bootstrap registration failed for IdpConfig {Id}", config.Id);
            }
        }

        logger.LogInformation("Auth: OidcSchemeBootstrap registered {Count} external auth schemes", enabled.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
