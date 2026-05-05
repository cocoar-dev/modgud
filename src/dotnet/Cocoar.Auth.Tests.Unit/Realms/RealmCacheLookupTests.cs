using Cocoar.Auth.Infrastructure.Realms;

namespace Cocoar.Auth.Tests.Unit.Realms;

/// <summary>
/// Pins the host → tenant resolution rules used by <c>RealmMiddleware</c>.
/// The single-realm-localhost-fallback in particular exists so devs can boot
/// the system without hosts-file entries — regressing it silently breaks
/// every fresh checkout.
/// </summary>
public class RealmCacheLookupTests
{
    private static readonly TenantInfo Acme = new("acme", IsControlPlane: false, IsActive: true);
    private static readonly TenantInfo System = new("system", IsControlPlane: true, IsActive: true);

    private static IReadOnlyDictionary<string, TenantInfo> Cache(params (string Host, TenantInfo Info)[] entries)
    {
        var dict = new Dictionary<string, TenantInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, info) in entries)
            dict[host] = info;
        return dict;
    }

    public class ExactHostMatch
    {
        [Fact]
        public void Returns_tenant_for_registered_domain()
        {
            var cache = Cache(("acme.localhost", Acme));

            var result = RealmCacheLookup.Resolve("acme.localhost", cache, singleActiveRealm: null);

            Assert.Same(Acme, result);
        }

        [Fact]
        public void Match_is_case_insensitive_when_dictionary_uses_ordinal_ignore_case()
        {
            // The production cache uses StringComparer.OrdinalIgnoreCase — host headers
            // come in arbitrary case and must still resolve.
            var cache = Cache(("Acme.Localhost", Acme));

            var result = RealmCacheLookup.Resolve("acme.localhost", cache, singleActiveRealm: null);

            Assert.Same(Acme, result);
        }

        [Fact]
        public void Returns_null_for_unknown_host_with_no_fallback()
        {
            var cache = Cache(("acme.localhost", Acme));

            var result = RealmCacheLookup.Resolve("evil.example.com", cache, singleActiveRealm: null);

            Assert.Null(result);
        }
    }

    public class LocalhostFallback
    {
        [Theory]
        [InlineData("localhost")]
        [InlineData("127.0.0.1")]
        [InlineData("0.0.0.0")]
        [InlineData("::1")]
        public void Returns_single_realm_for_localhost_variants(string host)
        {
            var cache = Cache();
            var result = RealmCacheLookup.Resolve(host, cache, singleActiveRealm: System);

            Assert.Same(System, result);
        }

        [Theory]
        [InlineData("Localhost")]
        [InlineData("LOCALHOST")]
        public void Localhost_match_is_case_insensitive(string host)
        {
            var cache = Cache();
            var result = RealmCacheLookup.Resolve(host, cache, singleActiveRealm: System);

            Assert.Same(System, result);
        }

        [Fact]
        public void Returns_null_for_localhost_when_zero_active_realms()
        {
            var cache = Cache();

            var result = RealmCacheLookup.Resolve("localhost", cache, singleActiveRealm: null);

            Assert.Null(result);
        }

        [Fact]
        public void Returns_null_for_localhost_when_multiple_active_realms_exist()
        {
            // Multi-tenant deployments must not silently pick a realm — the explicit
            // hosts mapping is the only way to disambiguate.
            var cache = Cache(("acme.localhost", Acme), ("system.localhost", System));

            var result = RealmCacheLookup.Resolve("localhost", cache, singleActiveRealm: null);

            Assert.Null(result);
        }

        [Fact]
        public void Exact_match_takes_precedence_over_localhost_fallback()
        {
            var fallback = System;
            var cache = Cache(("localhost", Acme));

            var result = RealmCacheLookup.Resolve("localhost", cache, singleActiveRealm: fallback);

            Assert.Same(Acme, result);
        }

        [Fact]
        public void Non_localhost_unknown_host_does_not_use_fallback()
        {
            var cache = Cache();

            var result = RealmCacheLookup.Resolve("intranet.example.com", cache, singleActiveRealm: System);

            Assert.Null(result);
        }
    }

    public class ArgumentValidation
    {
        [Fact]
        public void Null_hostname_throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RealmCacheLookup.Resolve(null!, Cache(), singleActiveRealm: null));
        }

        [Fact]
        public void Null_cache_throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RealmCacheLookup.Resolve("localhost", null!, singleActiveRealm: null));
        }
    }

    public class LocalhostHostsConstant
    {
        [Fact]
        public void Contains_the_expected_loopback_aliases()
        {
            Assert.Contains("localhost", RealmCacheLookup.LocalhostHosts);
            Assert.Contains("127.0.0.1", RealmCacheLookup.LocalhostHosts);
            Assert.Contains("0.0.0.0", RealmCacheLookup.LocalhostHosts);
            Assert.Contains("::1", RealmCacheLookup.LocalhostHosts);
        }

        [Fact]
        public void Is_case_insensitive()
        {
            Assert.Contains("LOCALHOST", RealmCacheLookup.LocalhostHosts);
        }
    }
}
