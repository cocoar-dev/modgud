namespace Modgud.Permissions.Abstractions;

/// <summary>
/// Internal claim types for federation v1. Shared here so the issuer
/// (ExternalLoginProcessor, Authentication slice), the carrier copy
/// (AuthorizationEndpoints, API), and the union point (PermissionService,
/// Authorization slice) all reference the SAME literal.
/// </summary>
public static class FederationClaimTypes
{
    /// <summary>
    /// Carries a session-derived ExternallyDrivable group GUID. One claim per
    /// group. Set on the sign-in cookie (ExternalLoginProcessor.Success), copied
    /// into the OpenIddict grant with NO destination, and unioned into
    /// resource_access at token-issuance/UserInfo time — NEVER itself emitted
    /// to the wire (the hub boundary). The session is the lease (decision D/E).
    /// </summary>
    public const string SessionGroup = "modgud:session-group";
}
