using OpenIddict.Abstractions;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// <see cref="IOAuthGrantRevoker"/> over the OpenIddict managers. Mirrors the
/// established revoke pattern in the end-session flow (find-by-subject then
/// <c>TryRevokeAsync</c>), but spans ALL clients rather than a single
/// (subject, client) pair — a deleted/deactivated user must lose every grant.
/// The managers resolve the active realm via the tenant-scoped Marten stores,
/// so revocation only ever touches the caller's realm DB.
/// </summary>
public sealed class OpenIddictGrantRevoker(
    IOpenIddictTokenManager tokenManager,
    IOpenIddictAuthorizationManager authorizationManager) : IOAuthGrantRevoker
{
    public async Task<int> RevokeTokensBySubjectAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subject)) return 0;

        var revoked = 0;
        await foreach (var token in tokenManager.FindBySubjectAsync(subject, ct))
        {
            if (await tokenManager.TryRevokeAsync(token, ct))
                revoked++;
        }
        return revoked;
    }

    public async Task<int> RevokeAuthorizationsBySubjectAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subject)) return 0;

        var revoked = 0;
        await foreach (var authorization in authorizationManager.FindBySubjectAsync(subject, ct))
        {
            if (await authorizationManager.TryRevokeAsync(authorization, ct))
                revoked++;
        }
        return revoked;
    }
}
