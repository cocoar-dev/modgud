namespace Cocoar.Auth.Client.AspNetCore.Distribution;

/// <summary>
/// Typed client for the Cocoar.Auth distribution API. Implementations
/// take care of forwarding the user's bearer token from the current
/// request and attaching the configured resource-server credentials
/// (<c>X-Resource-Server-Id</c> / <c>X-Resource-Server-Secret</c>).
///
/// <para>Caching lives one layer above (see <c>PermissionsCache</c>) so
/// implementations can stay stateless.</para>
/// </summary>
public interface IDistributionClient
{
    /// <summary>
    /// Calls <c>GET /api/v1/distribution/me-permissions</c> and returns
    /// the parsed response. Throws <see cref="HttpRequestException"/> on
    /// non-2xx responses (the caller decides whether to fall back to an
    /// empty grant set or surface the failure).
    /// </summary>
    Task<MePermissionsResponse> GetMePermissionsAsync(
        string userBearerToken,
        CancellationToken ct = default);
}
