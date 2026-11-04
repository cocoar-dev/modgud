using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// EP-01 — the OIDC scheme name was built from the provider GUID alone. Realms keep
/// separate databases and a manifest import may pin a provider's ID, so two realms can
/// hold a provider with the same GUID: registering one replaced the other realm's scheme
/// (options and realm mapping included), and unregistering one removed the other's.
/// The schemes are looked up outside any realm, so the request-path materializer stays
/// out of the picture.
/// </summary>
public partial class DynamicOidcSchemeManagerTests
{
    private const string OtherRealm = "ep01-other-realm";

    [Fact]
    public async Task The_same_provider_id_in_two_realms_keeps_two_schemes()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);
        var systemName = config.DisplayName;
        var otherName = $"Other-{Guid.NewGuid():N}"[..16];
        var manager = Factory.Services.GetRequiredService<DynamicOidcSchemeManager>();

        try
        {
            using (TenantContext.Enter(TenantConstants.SystemTenantId))
                await manager.RegisterAsync(config);
            config.DisplayName = otherName; // the other realm's copy under the same pinned id
            using (TenantContext.Enter(OtherRealm))
                await manager.RegisterAsync(config);

            var names = (await manager.GetRegisteredExternalSchemesAsync()).Select(s => s.DisplayName).ToList();
            Assert.Contains(systemName, names);
            Assert.Contains(otherName, names);
        }
        finally
        {
            config.Enabled = false;
            using (TenantContext.Enter(OtherRealm))
                await manager.RegisterAsync(config);
        }
    }

    [Fact]
    public async Task Disabling_in_one_realm_leaves_the_other_realms_scheme()
    {
        var config = await CreateEntraConfigAsync(enabled: true, withClientId: true);
        var systemName = config.DisplayName;
        var manager = Factory.Services.GetRequiredService<DynamicOidcSchemeManager>();

        using (TenantContext.Enter(TenantConstants.SystemTenantId))
            await manager.RegisterAsync(config);
        config.Enabled = false; // the other realm's copy under the same pinned id, disabled
        using (TenantContext.Enter(OtherRealm))
            await manager.RegisterAsync(config);

        var names = (await manager.GetRegisteredExternalSchemesAsync()).Select(s => s.DisplayName).ToList();
        Assert.Contains(systemName, names);
    }
}
