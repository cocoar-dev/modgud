using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modgud.Provisioning.TestKit;

/// <summary>
/// Thin client over the Modgud control-plane provisioning API. Wraps an
/// <see cref="HttpClient"/> the caller has already pointed at a running Modgud instance and
/// authenticated as a control-plane admin (cookie or bearer). The entry point is
/// <see cref="ImportRealmAsync"/>, which creates a fresh realm, fills it from a manifest and
/// hands back a disposable <see cref="ProvisionedRealm"/> handle.
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
    /// Provisions a brand-new realm: creates the realm named by <paramref name="realm"/>
    /// (its slug must not already exist), then fills it from <paramref name="manifest"/>.
    /// Returns a handle that hard-deletes the realm on dispose. Throws
    /// <see cref="ModgudProvisioningException"/> if the server rejects either step.
    ///
    /// <para>The realm shell is a SEPARATE argument because a manifest deliberately carries
    /// no realm identity — the same manifest file provisions any realm you name here. Two
    /// server calls, one method: the kit keeps the convenience without the file having to
    /// know where it will land.</para>
    ///
    /// <para>If the manifest step fails, the just-created realm is torn down before the
    /// exception propagates, so a failed provision leaves no orphan behind for the next
    /// test run. (The server itself no longer does this — driving the API directly keeps
    /// the realm so a corrected manifest can simply be re-applied.)</para>
    /// </summary>
    public async Task<ProvisionedRealm> ImportRealmAsync(
        RealmSpec realm, RealmManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(manifest);

        using (var created = await _http.PostAsJsonAsync("api/admin/realms", realm, JsonOptions, ct))
        {
            if (!created.IsSuccessStatusCode)
                await ThrowFromResponseAsync(created, "create-realm", realm.Slug, ct);
        }

        try
        {
            return new ProvisionedRealm(this, await ApplyAsync(realm.Slug, manifest, ct));
        }
        catch
        {
            // Best-effort teardown: never let a cleanup failure mask the real error.
            try { await HardDeleteAsync(realm.Slug, ct); } catch { /* ignored */ }
            throw;
        }
    }

    internal async Task<RealmImportResult> ApplyAsync(
        string slug, RealmManifest manifest, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/admin/realms/{slug}/apply", manifest, JsonOptions, ct);
        return await ReadResultOrThrowAsync(response, "apply", slug, ct);
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
            // Two error shapes reach this client, both using a field named "error":
            //   manifest endpoints  → { "Error": "<code>", "Message": "<description>" }
            //   canonical ErrorOr   → { "error": "<description>" }          (no code at all)
            // Telling them apart by whether "Message" is present keeps a description from
            // masquerading as an error code on the second shape.
            var error = JsonSerializer.Deserialize<ErrorBody>(body, JsonOptions);
            if (error?.Message is { Length: > 0 })
            {
                code = error.Error;
                message = error.Message;
            }
            else
            {
                message = error?.Error;
            }
        }
        catch (JsonException) { /* non-JSON body — fall back to the raw text below */ }

        throw new ModgudProvisioningException(response.StatusCode, op, slug, code,
            message ?? $"Realm {op} for '{slug}' failed with {(int)response.StatusCode}: {body}");
    }

    private sealed record ErrorBody(string? Error, string? Message);
}

/// <summary>The successful-provisioning response: the realm's slug + canonical host and the
/// plaintext secrets of any confidential clients (only available at create time).</summary>
public sealed record RealmImportResult
{
    public required string Slug { get; init; }
    public required string PrimaryDomain { get; init; }
    public Dictionary<string, string> ClientSecrets { get; init; } = [];
}

/// <summary>Thrown when the provisioning API rejects a create / apply / hard-delete. Carries
/// the HTTP status and the server's error <see cref="Code"/> (e.g. <c>Realm.AlreadyExists</c>,
/// <c>Realm.NotFound</c>, <c>Manifest.UnknownReference</c>) when present.</summary>
public sealed class ModgudProvisioningException(
    HttpStatusCode statusCode, string operation, string slug, string? code, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Operation { get; } = operation;
    public string Slug { get; } = slug;
    public string? Code { get; } = code;
}
