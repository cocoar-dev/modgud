using Microsoft.Extensions.DependencyInjection;

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
        // Wiring lands in subsequent commits on feat/saml-federation:
        //   - SamlFlavorRegistry (Generic + EntraID + ADFS)
        //   - DynamicSamlSchemeManager
        //   - SP cert generation / rotation services
        //   - Metadata refresh hosted service
        //
        // For now this is the placeholder hook so Program.cs can be wired
        // once and incremental commits only touch this file.
        return services;
    }
}
