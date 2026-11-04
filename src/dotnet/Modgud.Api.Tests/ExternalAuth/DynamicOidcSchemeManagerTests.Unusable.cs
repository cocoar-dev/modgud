using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// EP-05 — when an enabled provider's new configuration could not be registered (empty
/// ClientId, unknown flavor, endpoint derivation failure), RegisterAsync returned early
/// and left the OLD registration with its old options in place, while the materializer
/// counted the new state as processed. It now drops the old registration.
/// </summary>
public partial class DynamicOidcSchemeManagerTests
{
    [Fact]
    public async Task RegisterAsync_unusable_new_configuration_drops_the_old_scheme()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<DynamicOidcSchemeManager>();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();
        var name = DynamicOidcSchemeManager.SchemeNameFor(TenantConstants.SystemTenantId, config.Id);

        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(name));

        config.ClientId = string.Empty; // still enabled, no longer registrable
        using (TenantContext.Enter("system"))
            await manager.RegisterAsync(config);

        Assert.Null(await schemeProvider.GetSchemeAsync(name));
    }
}
