using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modgud.Provisioning.TestKit;

/// <summary>
/// Thin client over the Modgud control-plane provisioning API. Wraps an
/// <see cref="HttpClient"/> the caller has already pointed at a running Modgud instance and
/// authenticated as a control-plane admin (cookie or bearer). The only entry point is
/// <see cref="ImportRealmAsync"/>, which provisions a fresh realm and hands back a
/// disposable <see cref="ProvisionedRealm"/> handle.
/// </summary>
public sealed class ModgudProvisioningClient
{
    // Server (re)serialises PascalCase and omits null members; case-insensitive read keeps
    // us robust to either convention.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <param name="http">An <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/>
    /// is the Modgud instance and which already carries control-plane admin auth.</param>
    public ModgudProvisioningClient(HttpClient http)
        => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Provisions a brand-new realm from <paramref name="manifest"/> (the slug must not
    /// already exist) and returns a handle that hard-deletes the realm on dispose. Throws
    /// <see cref="ModgudProvisioningException"/> if the server rejects the import.
    /// </summary>
    public async Task<ProvisionedRealm> ImportRealmAsync(
        RealmManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var response = await _http.PostAsJsonAsync(
            "api/admin/realms/import", manifest, JsonOptions, ct);
        var result = await ReadResultOrThrowAsync(response, "import", manifest.Realm.Slug, ct);
        return new ProvisionedRealm(this, result);
    }

    internal async Task ApplyAsync(string slug, RealmManifest manifest, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/admin/realms/{slug}/apply", manifest, JsonOptions, ct);
        await ReadResultOrThrowAsync(response, "apply", slug, ct);
    }

    internal async Task HardDeleteAsync(string slug, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/admin/realms/{slug}?hard=true", ct);
        if (!response.IsSuccessStatusCode)
            await ThrowFromResponseAsync(response, "hard-delete", slug, ct);
    }

    private static async Task<RealmImportResult> ReadResultOrThrowAsync(
        HttpResponseMessage response, string op, string slug, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            await ThrowFromResponseAsync(response, op, slug, ct);

        var result = await response.Content.ReadFromJsonAsync<RealmImportResult>(JsonOptions, ct);
        return result ?? throw new ModgudProvisioningException(
            response.StatusCode, op, slug, code: null,
            $"Realm {op} for '{slug}' returned {(int)response.StatusCode} with an empty body.");
    }

    private static async Task ThrowFromResponseAsync(
        HttpResponseMessage response, string op, string slug, CancellationToken ct)
    {
        string? code = null;
        string? message = null;
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var error = JsonSerializer.Deserialize<ManifestErrorBody>(body, JsonOptions);
            code = error?.Error;
            message = error?.Message;
        }
        catch (JsonException) { /* non-JSON body — fall back to the raw text below */ }

        throw new ModgudProvisioningException(response.StatusCode, op, slug, code,
            message ?? $"Realm {op} for '{slug}' failed with {(int)response.StatusCode}: {body}");
    }

    private sealed record ManifestErrorBody(string? Error, string? Message);
}

/// <summary>The successful-provisioning response: the realm's slug + canonical host and the
/// plaintext secrets of any confidential clients (only available at create time).</summary>
public sealed record RealmImportResult
{
    public required string Slug { get; init; }
    public required string PrimaryDomain { get; init; }
    public Dictionary<string, string> ClientSecrets { get; init; } = [];
}

/// <summary>Thrown when the provisioning API rejects an import / apply / hard-delete. Carries
/// the HTTP status and the server's error <see cref="Code"/> (e.g. <c>Realm.AlreadyExists</c>,
/// <c>Realm.NotFound</c>, <c>Manifest.SlugMismatch</c>) when present.</summary>
public sealed class ModgudProvisioningException(
    HttpStatusCode statusCode, string operation, string slug, string? code, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Operation { get; } = operation;
    public string Slug { get; } = slug;
    public string? Code { get; } = code;
}
