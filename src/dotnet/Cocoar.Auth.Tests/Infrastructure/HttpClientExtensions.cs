using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;

namespace Cocoar.Auth.Tests.Infrastructure;

public static class HttpClientExtensions
{
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

        return await client.PostAsJsonAsync($"/realms/{realmSlug}/api/auth/login", loginDto, jsonOptions);
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
