using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Tests.Unit.ExternalAuth;

/// <summary>
/// Pure-construction tests for <see cref="DynamicSamlSchemeManager"/>. The
/// only dependencies are the flavor registry and a logger, so no integration
/// scaffolding is needed — sister tests in <c>Modgud.Api.Tests</c> cover the
/// event-handler + bootstrap wire-up under a live ASP.NET Core host.
/// </summary>
public class DynamicSamlSchemeManagerTests
{
    private const string Realm = "acme";

    private static DynamicSamlSchemeManager NewManager() =>
        new(
            new SamlFlavorRegistry(new ISamlFlavor[]
            {
                new GenericSamlFlavor(),
                new EntraIdSamlFlavor(),
                new AdfsSamlFlavor(),
            }),
            new SamlMetadataFetcher(new NoNetworkHttpClientFactory(), NullLogger<SamlMetadataFetcher>.Instance),
            NullLogger<DynamicSamlSchemeManager>.Instance);

    /// <summary>
    /// Test double — returns an HttpClient that fails every request. Sufficient
    /// for the manager tests because none of them set FlavorData.MetadataUrl,
    /// so the fetcher is never invoked. Defends the test setup against a
    /// future refactor that DOES trigger the fetch: it would surface as a
    /// clear failure instead of silently calling out to the internet.
    /// </summary>
    private sealed class NoNetworkHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new FailingHandler()) { Timeout = TimeSpan.FromSeconds(1) };

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException(
                    "Network access not allowed in unit tests — set FlavorData.MetadataUrl " +
                    "only in tests that explicitly need to exercise the fetcher.");
        }
    }

    private static LoginProvider NewSamlProvider(
        Guid? id = null,
        string flavor = LoginProviderFlavor.GenericSaml,
        bool enabled = true,
        bool deleted = false,
        string displayName = "Test SAML",
        JsonDocument? flavorData = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            DisplayName = displayName,
            Type = LoginProviderType.Saml,
            Flavor = flavor,
            Enabled = enabled,
            IsDeleted = deleted,
            FlavorData = flavorData,
        };

    public class Registration
    {
        [Fact]
        public async Task Registers_enabled_saml_provider_into_cache()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var provider = NewSamlProvider();

            await manager.RegisterAsync(provider);

            Assert.True(manager.TryGet(provider.Id, out var entry));
            Assert.NotNull(entry);
            Assert.Equal(provider.Id, entry!.LoginProviderId);
            Assert.Equal(Realm, entry.RealmSlug);
            Assert.Equal(LoginProviderFlavor.GenericSaml, entry.Flavor);
        }

        [Fact]
        public async Task Disabled_provider_evicts_from_cache()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var provider = NewSamlProvider();

            await manager.RegisterAsync(provider);
            Assert.True(manager.TryGet(provider.Id, out _));

            provider.Enabled = false;
            await manager.RegisterAsync(provider);
            Assert.False(manager.TryGet(provider.Id, out _));
        }

        [Fact]
        public async Task Deleted_provider_evicts_from_cache()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var provider = NewSamlProvider();

            await manager.RegisterAsync(provider);

            provider.IsDeleted = true;
            await manager.RegisterAsync(provider);

            Assert.False(manager.TryGet(provider.Id, out _));
        }

        [Fact]
        public async Task Non_saml_type_is_ignored()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var oidc = NewSamlProvider();
            oidc.Type = LoginProviderType.Oidc;

            await manager.RegisterAsync(oidc);

            Assert.False(manager.TryGet(oidc.Id, out _));
        }

        [Fact]
        public async Task Unknown_flavor_logs_and_skips_silently()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var unknown = NewSamlProvider(flavor: "FutureFlavor");

            // Must not throw — silent skip is the contract.
            await manager.RegisterAsync(unknown);

            Assert.False(manager.TryGet(unknown.Id, out _));
        }

        [Fact]
        public async Task EntraID_flavor_applies_microsoft_claim_uris_to_cached_data()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var provider = NewSamlProvider(flavor: LoginProviderFlavor.EntraIdSaml);

            await manager.RegisterAsync(provider);

            Assert.True(manager.TryGet(provider.Id, out var entry));
            Assert.Contains(
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                entry!.FlavorData.AttributeMap["email"]);
        }

        [Fact]
        public async Task Re_registration_overwrites_cache_entry()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var id = Guid.NewGuid();

            await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "First"));
            await manager.RegisterAsync(NewSamlProvider(id: id, displayName: "Renamed"));

            Assert.True(manager.TryGet(id, out var entry));
            Assert.Equal("Renamed", entry!.DisplayName);
        }

        [Fact]
        public async Task Register_without_tenant_context_throws()
        {
            // No TenantContext.Enter(...) on purpose.
            var manager = NewManager();
            var provider = NewSamlProvider();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.RegisterAsync(provider));
        }
    }

    public class Lookup
    {
        [Fact]
        public void TryGet_returns_false_for_unknown_id()
        {
            var manager = NewManager();
            Assert.False(manager.TryGet(Guid.NewGuid(), out _));
        }

        [Fact]
        public async Task GetRegisteredForRealm_filters_by_slug()
        {
            var manager = NewManager();

            using (TenantContext.Enter("acme"))
                await manager.RegisterAsync(NewSamlProvider(displayName: "acme-saml"));

            using (TenantContext.Enter("globex"))
                await manager.RegisterAsync(NewSamlProvider(displayName: "globex-saml"));

            var acmeOnly = manager.GetRegisteredForRealm("acme");
            Assert.Single(acmeOnly);
            Assert.Equal("acme-saml", acmeOnly[0].DisplayName);
        }

        [Fact]
        public async Task GetAllRegistered_returns_across_realms()
        {
            var manager = NewManager();

            using (TenantContext.Enter("acme"))
                await manager.RegisterAsync(NewSamlProvider());

            using (TenantContext.Enter("globex"))
                await manager.RegisterAsync(NewSamlProvider());

            Assert.Equal(2, manager.GetAllRegistered().Count);
        }
    }

    public class Unregistration
    {
        [Fact]
        public async Task UnregisterAsync_removes_entry()
        {
            using var tenantScope = TenantContext.Enter(Realm);
            var manager = NewManager();
            var provider = NewSamlProvider();

            await manager.RegisterAsync(provider);
            await manager.UnregisterAsync(provider.Id);

            Assert.False(manager.TryGet(provider.Id, out _));
        }

        [Fact]
        public async Task UnregisterAsync_is_idempotent_for_unknown_id()
        {
            var manager = NewManager();

            // Must not throw.
            await manager.UnregisterAsync(Guid.NewGuid());
        }
    }
}
