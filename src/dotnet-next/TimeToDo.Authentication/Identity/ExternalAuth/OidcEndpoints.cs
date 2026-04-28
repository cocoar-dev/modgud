namespace TimeToDo.Authentication.Identity.ExternalAuth;

/// <summary>
/// OIDC endpoint set for an IdP instance. Flavors derive this from their
/// flavor-data (e.g. Entra: from TenantId) or accept a direct metadata URI.
/// <para>
/// If <see cref="MetadataUri"/> is set, the OIDC handler will fetch
/// <c>.well-known/openid-configuration</c> from there and ignore the explicit
/// endpoints. If <see cref="MetadataUri"/> is <c>null</c>, the handler uses the
/// explicit endpoints — useful for IdPs without discovery or for tests.
/// </para>
/// </summary>
public record OidcEndpoints(
    string Authority,
    string? MetadataUri = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? UserInfoEndpoint = null,
    string? EndSessionEndpoint = null);
