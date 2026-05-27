using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml.Flavors;

namespace Modgud.Authentication.Setup;

/// <summary>
/// SAML 2.0 SP federation wiring. Mirrors the OIDC external-auth setup
/// (<see cref="Modgud.Authentication.Api.ExternalAuth"/>) — flavor registry,
/// dynamic per-realm scheme manager, and SP signing/encryption cert
/// management. Implementation lands incrementally across the SAML wave;
/// see <c>dev-docs/future-features/saml-federation.md</c>.
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

        // Still to come in subsequent commits on feat/saml-federation:
        //   - DynamicSamlSchemeManager
        //   - SP cert generation / rotation services
        //   - Metadata refresh hosted service
        return services;
    }
}
