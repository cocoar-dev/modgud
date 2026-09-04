using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Modgud.AspNetCore.ResourceServer;

internal static class ModgudSessionFeedDefaults
{
    public const string HttpClientName = "Modgud.ResourceServer.SessionFeed";
    public const string ManagementScope = "modgud.management";
    public const string ManagementResource = "urn:modgud:management-api";
    public const string SessionEntityKind = "session";
}

/// <summary>Outcome of one feed read.</summary>
internal sealed record SessionFeedBatch(
    IReadOnlyList<SessionFeedMessage> Messages,
    string? LastCursor,
    bool HasMore,
    bool ResetRequired,
    bool FeedEnded);

internal sealed record SessionFeedMessage(string Kind, string? ChangeKind, string? EntityKind, string? SessionId, string? Reason);

/// <summary>
/// The HTTP side of session revocation: a cached <c>client_credentials</c> token for
/// the Management API, the snapshot (for a fresh cursor) and the polling read of the
/// Application change feed. Only what the denylist needs is parsed.
/// </summary>
internal sealed class ModgudSessionFeedClient(
    IHttpClientFactory httpClients,
    ModgudResourceServerOptions resourceServer,
    ModgudSessionRevocationOptions options,
    TimeProvider clock,
    ILogger<ModgudSessionFeedClient> logger)
{
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    private string Authority => resourceServer.Authority.TrimEnd('/');
    private string ClientId => options.ClientId ?? resourceServer.IntrospectionClientId ?? string.Empty;
    private string ClientSecret => options.ClientSecret ?? resourceServer.IntrospectionClientSecret ?? string.Empty;

    /// <summary>Returns the cursor to resume from (the snapshot's checkpoint). The
    /// entities themselves are not needed: only ends matter for a denylist.</summary>
    public async Task<string> SnapshotCursorAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Authority}/api/app/{options.AppId}/change-feed/snapshot", ct);
        await EnsureSuccessAsync(response, "snapshot", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("Cursor").GetString()
               ?? throw new InvalidOperationException("Modgud: the change-feed snapshot carried no cursor.");
    }

    public async Task<SessionFeedBatch> ReadAsync(string cursor, CancellationToken ct)
    {
        var limit = Math.Clamp(options.BatchSize, 1, 500);
        var url = $"{Authority}/api/app/{options.AppId}/change-feed?cursor={Uri.EscapeDataString(cursor)}&limit={limit}";
        using var response = await SendAsync(HttpMethod.Get, url, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // ScopeChanged / CursorTooOld / FeedInitializing: start over from a snapshot.
            logger.LogInformation("Modgud: change feed asked for a new snapshot ({Body})", await response.Content.ReadAsStringAsync(ct));
            return new SessionFeedBatch([], null, false, ResetRequired: true, FeedEnded: false);
        }
        await EnsureSuccessAsync(response, "read", ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var messages = new List<SessionFeedMessage>();
        string? lastCursor = null;
        var reset = false;
        var ended = root.TryGetProperty("FeedEnded", out var fe) && fe.ValueKind == JsonValueKind.True;
        foreach (var message in root.GetProperty("Messages").EnumerateArray())
        {
            var kind = message.GetProperty("Kind").GetString() ?? string.Empty;
            lastCursor = message.GetProperty("Cursor").GetString() ?? lastCursor;
            if (kind == "ResetRequired") reset = true;
            if (kind == "FeedEnded") ended = true;
            if (kind != "Change") continue;

            string? sessionId = null;
            string? reason = message.TryGetProperty("Reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (message.TryGetProperty("Payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("SessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString();

            messages.Add(new SessionFeedMessage(
                kind,
                message.TryGetProperty("ChangeKind", out var ck) ? ck.GetString() : null,
                message.TryGetProperty("EntityKind", out var ek) ? ek.GetString() : null,
                sessionId,
                reason));
        }

        var hasMore = root.TryGetProperty("HasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
        return new SessionFeedBatch(messages, lastCursor, hasMore, reset, ended);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await httpClients.CreateClient(ModgudSessionFeedDefaults.HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The cached token was revoked or rotated away; fetch a fresh one once.
            _accessToken = null;
            response.Dispose();
            token = await GetAccessTokenAsync(ct);
            using var retry = new HttpRequestMessage(method, url);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await httpClients.CreateClient(ModgudSessionFeedDefaults.HttpClientName)
                .SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_accessToken is not null && _accessTokenExpiresAt > now.AddSeconds(30))
            return _accessToken;

        using var content = new FormUrlEncodedContent(
        [
            new("grant_type", "client_credentials"),
            new("client_id", ClientId),
            new("client_secret", ClientSecret),
            new("scope", ModgudSessionFeedDefaults.ManagementScope),
            new("resource", ModgudSessionFeedDefaults.ManagementResource),
        ]);
        using var response = await httpClients.CreateClient(ModgudSessionFeedDefaults.HttpClientName)
            .PostAsync($"{Authority}/connect/token", content, ct);
        await EnsureSuccessAsync(response, "token", ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _accessToken = document.RootElement.GetProperty("access_token").GetString()
                       ?? throw new InvalidOperationException("Modgud: the token response carried no access_token.");
        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 300;
        _accessTokenExpiresAt = now.AddSeconds(expiresIn);
        return _accessToken;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Modgud: change-feed {what} request failed with {(int)response.StatusCode}: {Truncate(body)}",
            null,
            response.StatusCode);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
