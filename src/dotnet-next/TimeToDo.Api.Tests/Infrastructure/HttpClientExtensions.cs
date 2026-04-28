using System.Text.Json;

namespace TimeToDo.Api.Tests.Infrastructure;

public static class HttpClientExtensions
{
    /// <summary>
    /// Reads the response content and deserializes it to the specified type
    /// </summary>
    public static async Task<T?> ReadFromJsonAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions jsonOptions)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(content))
            return default;

        return JsonSerializer.Deserialize<T>(content, jsonOptions);
    }

    /// <summary>
    /// Asserts the response is successful and returns the deserialized content
    /// </summary>
    public static async Task<T> ReadSuccessJsonAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions jsonOptions) where T : class
    {
        response.EnsureSuccessStatusCode();
        var result = await response.ReadFromJsonAsync<T>(jsonOptions);
        Assert.NotNull(result);
        return result;
    }
}
