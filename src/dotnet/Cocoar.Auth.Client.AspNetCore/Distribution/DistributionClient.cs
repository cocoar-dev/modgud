using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Client.AspNetCore.Distribution;

/// <summary>
/// Default <see cref="IDistributionClient"/> implementation. Built on top
/// of a named <see cref="HttpClient"/> registered via
/// <c>AddHttpClient&lt;IDistributionClient, DistributionClient&gt;</c> in
/// <c>ServiceCollectionExtensions</c>.
/// </summary>
internal sealed class DistributionClient : IDistributionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly CocoarAuthOptions _options;

    public DistributionClient(HttpClient http, IOptions<CocoarAuthOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<MePermissionsResponse> GetMePermissionsAsync(
        string userBearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userBearerToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/distribution/me-permissions");

        // Forward the user-bearer-token from the incoming request — the
        // distribution API needs both the user's identity (this header)
        // AND the RS's credentials (next two headers) to know who's
        // asking and on whose behalf.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userBearerToken);
        request.Headers.Add("X-Resource-Server-Id", _options.ResourceServerId);
        request.Headers.Add("X-Resource-Server-Secret", _options.ResourceServerSecret);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<MePermissionsResponse>(JsonOptions, ct)
            ?? throw new HttpRequestException(
                "Cocoar.Auth distribution API returned a successful status but empty body.");
        return payload;
    }
}
