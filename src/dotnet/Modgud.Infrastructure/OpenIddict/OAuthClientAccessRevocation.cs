namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// E.clients-B3 — what a deleted or disabled OAuth client loses: every token issued to
/// it and every authorization (consent grant) made for it, across all subjects. Token
/// introspection checks the token entry, not whether its client still exists or is
/// enabled, so without this a reference access token of a deleted or disabled client
/// stayed active until it expired. Regenerating a client's secret deliberately does NOT
/// revoke (decision 2026-11-03): rotation must not sign every user of the app out.
/// Inside a manifest apply the deferring revoker runs this after the commit.
/// </summary>
public static class OAuthClientAccessRevocation
{
    public static async Task RevokeClientAccessAsync(
        this IOAuthGrantRevoker revoker, string applicationId, CancellationToken ct = default)
    {
        await revoker.RevokeTokensByApplicationIdAsync(applicationId, ct);
        await revoker.RevokeAuthorizationsByApplicationIdAsync(applicationId, ct);
    }
}
