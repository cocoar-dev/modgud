using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// EP-01 — the SAML cache was keyed by the provider GUID alone. Realms keep separate
/// databases and a manifest import may pin a provider's ID, so two realms can hold a
/// provider with the same GUID: registering one replaced the other's entry, and
/// unregistering one (disable / delete) removed the other's.
/// </summary>
public partial class DynamicSamlSchemeManagerTests
{
    public class CrossRealm
    {
        [Fact]
        public async Task The_same_provider_id_in_two_realms_keeps_two_registrations()
        {
            var manager = NewManager();
            var id = Guid.NewGuid();

            using (TenantContext.Enter("acme"))
                await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "acme-saml", slug: "shared"));
            using (TenantContext.Enter("globex"))
                await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "globex-saml", slug: "shared"));

            Assert.True(manager.TryGetBySlug("acme", "shared", out var acme));
            Assert.Equal("acme-saml", acme!.DisplayName);
            Assert.True(manager.TryGetBySlug("globex", "shared", out var globex));
            Assert.Equal("globex-saml", globex!.DisplayName);
        }

        [Fact]
        public async Task Unregistering_in_one_realm_leaves_the_other_realms_provider()
        {
            var manager = NewManager();
            var id = Guid.NewGuid();

            using (TenantContext.Enter("acme"))
                await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "acme-saml", slug: "shared"));
            using (TenantContext.Enter("globex"))
            {
                await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "globex-saml", slug: "shared"));
                await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "globex-saml", slug: "shared", enabled: false));
            }

            Assert.True(manager.TryGetBySlug("acme", "shared", out var acme));
            Assert.Equal("acme-saml", acme!.DisplayName);
            Assert.False(manager.TryGetBySlug("globex", "shared", out _));
        }
    }
}
