using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;

namespace Cocoar.Auth.Tests.Infrastructure;

public static class HttpClientExtensions
{
    /// <summary>
    /// Login via the current client's Host header (default: system.localhost).
    /// </summary>
    public static async Task<HttpResponseMessage> LoginAsync(
        this HttpClient client,
        string userName,
        string password,
        JsonSerializerOptions jsonOptions)
    {
        var loginDto = new LoginDto
        {
            UserName = userName,
            Password = password,
            RememberMe = false
        };

        return await client.PostAsJsonAsync("/api/auth/login", loginDto, jsonOptions);
    }

    /// <summary>
    /// Login in a specific realm by temporarily setting the Host header.
    /// </summary>
    public static async Task<HttpResponseMessage> LoginInRealmAsync(
        this HttpClient client,
        string realmSlug,
        string userName,
        string password,
        JsonSerializerOptions jsonOptions)
    {
        var loginDto = new LoginDto
        {
            UserName = userName,
            Password = password,
            RememberMe = false
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(loginDto, options: jsonOptions)
        };
        request.Headers.Host = $"{realmSlug}.localhost";

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Sets the default Host header for all subsequent requests from this client.
    /// </summary>
    public static HttpClient WithHost(this HttpClient client, string hostname)
    {
        client.DefaultRequestHeaders.Host = hostname;
        return client;
    }

    public static async Task<T?> ReadFromJsonAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions jsonOptions)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(content))
            return default;

        return JsonSerializer.Deserialize<T>(content, jsonOptions);
    }
}
