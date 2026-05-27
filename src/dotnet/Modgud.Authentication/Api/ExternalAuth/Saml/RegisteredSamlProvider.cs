using Modgud.Authentication.Identity.LoginProviders.Saml;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// One cached, ready-to-use SAML SP configuration for a single
/// <c>LoginProvider</c>. Stored in <see cref="DynamicSamlSchemeManager"/>'s
/// in-memory cache so endpoint handlers (login / acs / metadata) avoid a
/// Marten round-trip per SAML request.
/// <para>
/// <see cref="IdpMetadata"/> is the IdP-side parsed metadata (EntityID,
/// signing certs, SSO URLs). Null when the IdP hasn't been reached yet —
/// endpoint handlers reject login attempts in that case with a clear error
/// rather than crashing on a half-configured provider.
/// </para>
/// </summary>
public sealed record RegisteredSamlProvider(
    Guid LoginProviderId,
    string DisplayName,
    string Flavor,
    string RealmSlug,
    SamlFlavorData FlavorData,
    SamlIdpMetadata? IdpMetadata,
    DateTimeOffset? MetadataFetchedAt);
