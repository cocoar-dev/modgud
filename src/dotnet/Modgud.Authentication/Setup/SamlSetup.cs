using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

namespace Modgud.Authentication.Setup;

/// <summary>
/// SAML 2.0 SP federation wiring. Mirrors the OIDC external-auth setup
/// (<see cref="Modgud.Authentication.Api.ExternalAuth"/>) — flavor registry,
/// dynamic per-realm scheme manager, and SP signing/encryption cert
/// management. See the SAML federation documentation for the supported
/// SP-initiated surface and explicit v1 boundaries.
/// </summary>
public static class SamlSetup
{
    /// <summary>
    /// Registers SAML SP federation services in DI. Called from
    /// <c>Program.cs</c> alongside the OIDC external-auth registrations.
    /// </summary>
    public static IServiceCollection AddModgudSaml(this IServiceCollection services)
    {
        // SAML flavor registry — three concrete flavors, one parallel
        // registry to the OIDC LoginProviderFlavorRegistry. Mirrors the
        // OIDC DI shape exactly so admin UI and event-handler patterns
        // stay symmetric across protocols.
        services.AddSingleton<ISamlFlavor, GenericSamlFlavor>();
        services.AddSingleton<ISamlFlavor, EntraIdSamlFlavor>();
        services.AddSingleton<ISamlFlavor, AdfsSamlFlavor>();
        services.AddSingleton<SamlFlavorRegistry>();

        // Per-provider Saml2Configuration cache (mirror of
        // DynamicOidcSchemeManager). Singleton because the cache state is
        // process-wide and protected by ConcurrentDictionary.
        // IdP metadata fetcher — named HttpClient under IHttpClientFactory
        // so the fetcher itself can stay singleton (matches the singleton-
        // DynamicSamlSchemeManager that uses it) while still benefiting
        // from the framework's connection pooling + handler rotation.
        // The metadata URL is admin-supplied, so the fetch is an SSRF sink: it
        // gets the same transport-level guard as the CIMD fetcher (DNS resolved
        // + validated before connect, redirects off, tight timeouts). A realm
        // admin is a lower-trust tier than the platform operator, so
        // "an admin configured it" is not a reason to skip this.
        services.AddHttpClient(SamlMetadataFetcher.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
                Modgud.Infrastructure.Http.SsrfSafeHttpHandlerFactory.Create(
                    "SAML metadata fetch", sp.GetRequiredService<Modgud.Infrastructure.Http.SsrfAllowList>()));
        services.AddSingleton<SamlMetadataFetcher>();

        services.AddSingleton<DynamicSamlSchemeManager>();

        // SP cert store + per-realm certificate service. Store is singleton
        // (creates the DataProtection protector once); service is scoped
        // because it consumes IDocumentSession which is scoped.
        services.AddSingleton<SamlSpCertificateStore>();
        services.AddScoped<SamlSpCertificateService>();

        // Per-request SP config builder + protocol flow (AuthnRequest /
        // ACS / SP metadata XML). Scoped because they consume scoped
        // services (cert service, SignInManager, processor, session
        // service) and run within an HTTP request.
        services.AddScoped<SamlContextBuilder>();
        services.AddScoped<SamlLoginFlow>();

        // AuthnRequest correlation store (InResponseTo + single use). Scoped —
        // it consumes the tenant-scoped IDocumentSession.
        services.AddScoped<ISamlAuthnRequestStore, MartenSamlAuthnRequestStore>();

        // SamlContextBuilder needs IHttpContextAccessor to derive the SP
        // EntityID + ACS URL from the current request — register if not
        // already done elsewhere in the host setup (it's idempotent).
        services.AddHttpContextAccessor();

        // Cold-start warm-up and runtime changes both go through
        // LoginProviderSchemeMaterializer (registered by the host together
        // with the OIDC side) — every node resolves SAML providers from the
        // database on demand (ADR 0022, D6).

        // Periodic metadata refresh — wakes every 15 min, re-fetches IdP
        // metadata for any provider whose per-provider cadence has elapsed.
        // Picks up IdP cert rotations ahead of the activation date most
        // IdPs advertise the new key from.
        services.AddHostedService<SamlMetadataRefreshService>();

        return services;
    }
}
