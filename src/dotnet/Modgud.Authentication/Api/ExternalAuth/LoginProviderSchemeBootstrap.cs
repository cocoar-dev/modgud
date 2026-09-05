using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Cold-start warm-up: materialises every active realm's OIDC schemes and SAML
/// providers on this node so the first external login after boot does not pay
/// for the metadata fetch. Purely an optimisation — the request path resolves
/// providers from the database on demand either way (ADR 0010, D6), so a realm
/// that fails here is retried on its first request.
/// <para>
/// Replaces the former <c>OidcSchemeBootstrap</c> and <c>SamlSchemeBootstrap</c>,
/// which were the only way a scheme reached this node's memory and therefore
/// broke the moment a provider was created on a different node.
/// </para>
/// </summary>
public sealed class LoginProviderSchemeBootstrap(
    LoginProviderSchemeMaterializer materializer,
    IRealmCache realmCache,
    ILogger<LoginProviderSchemeBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var realms = await realmCache.GetAllActiveAsync();
        foreach (var realm in realms)
        {
            try
            {
                await materializer.RefreshAsync(realm.Slug, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Login-provider warm-up failed for realm {Realm}; providers resolve on first request", realm.Slug);
            }
        }

        logger.LogInformation("Login-provider warm-up finished for {Realms} realm(s)", realms.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
