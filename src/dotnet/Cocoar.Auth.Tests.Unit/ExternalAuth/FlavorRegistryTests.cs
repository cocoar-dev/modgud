using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth.Flavors;

namespace Cocoar.Auth.Tests.Unit.ExternalAuth;

/// <summary>
/// Pure-construction tests for <see cref="FlavorRegistry"/> — no DI container,
/// flavors are passed directly into the constructor. Sister integration tests
/// in <c>Cocoar.Auth.Api.Tests</c> verify DI-registered wire-up.
/// </summary>
public class FlavorRegistryTests
{
    private static FlavorRegistry NewRegistry(params IIdentityProviderFlavor[] flavors) => new(flavors);

    private static FlavorRegistry NewDefaultRegistry() =>
        NewRegistry(new EntraIdFlavor(), new GenericOidcFlavor());

    public class Resolution
    {
        [Fact]
        public void Get_returns_the_flavor_registered_for_the_key()
        {
            var entra = new EntraIdFlavor();
            var generic = new GenericOidcFlavor();
            var registry = NewRegistry(entra, generic);

            Assert.Same(entra, registry.Get(IdpFlavor.EntraId));
            Assert.Same(generic, registry.Get(IdpFlavor.GenericOidc));
        }

        [Fact]
        public void Get_lookup_is_case_insensitive()
        {
            var registry = NewDefaultRegistry();

            Assert.Same(registry.Get("EntraId"), registry.Get("entraid"));
            Assert.Same(registry.Get("EntraId"), registry.Get("ENTRAID"));
        }

        [Fact]
        public void Get_throws_KeyNotFoundException_for_unknown_keys()
        {
            var registry = NewDefaultRegistry();

            var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("Okta"));
            Assert.Contains("Okta", ex.Message);
            // Error message lists known keys to help the operator.
            Assert.Contains(IdpFlavor.EntraId, ex.Message);
            Assert.Contains(IdpFlavor.GenericOidc, ex.Message);
        }

        [Fact]
        public void TryGet_returns_true_and_outputs_the_flavor_for_known_keys()
        {
            var registry = NewDefaultRegistry();

            Assert.True(registry.TryGet(IdpFlavor.EntraId, out var flavor));
            Assert.IsType<EntraIdFlavor>(flavor);
        }

        [Fact]
        public void TryGet_lookup_is_case_insensitive()
        {
            var registry = NewDefaultRegistry();

            Assert.True(registry.TryGet("entraid", out var flavor));
            Assert.IsType<EntraIdFlavor>(flavor);
        }

        [Fact]
        public void TryGet_returns_false_for_unknown_keys()
        {
            var registry = NewDefaultRegistry();

            Assert.False(registry.TryGet("Okta", out var flavor));
            Assert.Null(flavor);
        }
    }

    public class Enumeration
    {
        [Fact]
        public void All_exposes_every_registered_flavor()
        {
            var registry = NewDefaultRegistry();

            var keys = registry.All.Select(f => f.Key).ToList();

            Assert.Contains(IdpFlavor.EntraId, keys);
            Assert.Contains(IdpFlavor.GenericOidc, keys);
            Assert.Equal(2, keys.Count);
        }

        [Fact]
        public void Empty_registry_yields_an_empty_All_sequence()
        {
            var registry = NewRegistry();

            Assert.Empty(registry.All);
        }
    }

    public class Construction
    {
        [Fact]
        public void Throws_ArgumentException_when_two_flavors_share_a_key()
        {
            // ToDictionary throws ArgumentException on duplicate keys — pinned so
            // a future refactor cannot silently swallow a misconfiguration.
            Assert.Throws<ArgumentException>(() =>
                NewRegistry(new EntraIdFlavor(), new EntraIdFlavor()));
        }

        [Fact]
        public void Throws_ArgumentException_for_duplicate_keys_with_different_casing()
        {
            // Lookup is OrdinalIgnoreCase, so "EntraId" and "entraid" must
            // collide on construction.
            Assert.Throws<ArgumentException>(() =>
                NewRegistry(new EntraIdFlavor(), new StubFlavor("entraid")));
        }

        private sealed class StubFlavor : IIdentityProviderFlavor
        {
            public StubFlavor(string key) => Key = key;
            public string Key { get; }
            public string DisplayName => Key;
            public string DefaultIconName => "stub";
            public IReadOnlyList<string> DefaultScopes { get; } = Array.Empty<string>();
            public string DefaultUserUpdateScript => "(claims) => ({})";
            public bool DefaultStoreRawClaims => false;
            public IReadOnlyList<FlavorConfigField> ConfigSchema { get; } = Array.Empty<FlavorConfigField>();
            public OidcEndpoints DeriveEndpoints(System.Text.Json.JsonDocument? flavorData) =>
                throw new NotSupportedException();
        }
    }
}
