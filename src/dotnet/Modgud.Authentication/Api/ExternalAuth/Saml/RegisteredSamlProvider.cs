using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// One cached, ready-to-use SAML SP configuration for a single
/// <c>LoginProvider</c>. Stored in <see cref="DynamicSamlSchemeManager"/>'s
/// in-memory cache so endpoint handlers (login / acs / metadata) avoid a
/// Marten round-trip per SAML request.
/// </summary>
public sealed record RegisteredSamlProvider(
    Guid LoginProviderId,
    string DisplayName,
    string Flavor,
    string RealmSlug,
    SamlFlavorData FlavorData);
