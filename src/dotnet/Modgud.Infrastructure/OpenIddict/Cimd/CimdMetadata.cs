using System.Text.Json;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// The validated, public-client subset of a CIMD metadata document
/// (<c>draft-ietf-oauth-client-id-metadata-document</c> + RFC 7591). Only
/// the fields Modgud needs to synthesize an <c>OAuthApplicationState</c> are
/// retained; everything else in the document is ignored.
/// </summary>
public sealed record CimdMetadata
{
    public required string ClientId { get; init; }
    public string? ClientName { get; init; }
    public required IReadOnlyList<string> RedirectUris { get; init; }
    public required IReadOnlyList<string> GrantTypes { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
}

/// <summary>Outcome of validating a fetched CIMD document against the
/// <c>client_id</c> URL and the v1 (public-only) policy.</summary>
public abstract record CimdValidationResult
{
    public sealed record Valid(CimdMetadata Metadata) : CimdValidationResult;

    /// <param name="Reason">Machine-stable, log-safe description of which
    /// rule the document violated. Never surfaced to the client (CIMD has no
    /// error channel — the authorize request just fails as "unknown
    /// client").</param>
    public sealed record Invalid(string Reason) : CimdValidationResult;
}

/// <summary>
/// Pure validator for a fetched CIMD document. No HTTP, no DB — the resolver
/// fetches the bytes (SSRF-guarded) and hands them here. Unit-testable in
/// isolation; this is where the draft-spec document rules live.
/// </summary>
public static class CimdMetadataParser
{
    private const string AuthMethodNone = "none";

    private static readonly HashSet<string> AllowedGrantTypes = new(StringComparer.Ordinal)
    {
        "authorization_code",
        "refresh_token",
    };

    private static readonly HashSet<string> AllowedResponseTypes = new(StringComparer.Ordinal)
    {
        "code",
    };

    /// <param name="requestedClientId">The exact <c>client_id</c> string the
    /// client presented at <c>/authorize</c> — the document's own
    /// <c>client_id</c> MUST string-equal it (RFC 3986 §6.2.1).</param>
    public static CimdValidationResult Parse(string json, string requestedClientId)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Invalid("document is not valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return Invalid("document is not a JSON object.");

        // ── client_id MUST equal the dereferenced URL (exact string) ──────
        if (!TryGetString(root, "client_id", out var docClientId) || docClientId is null)
            return Invalid("document is missing the required client_id field.");
        if (!string.Equals(docClientId, requestedClientId, StringComparison.Ordinal))
            return Invalid("document client_id does not match the client_id URL.");

        // ── public-only (v1): no shared-secret auth, no secret at rest ────
        if (root.TryGetProperty("client_secret", out _))
            return Invalid("document must not contain a client_secret (CIMD clients are public).");

        if (TryGetString(root, "token_endpoint_auth_method", out var authMethod) && authMethod is not null
            && !string.Equals(authMethod, AuthMethodNone, StringComparison.Ordinal))
        {
            return Invalid($"token_endpoint_auth_method '{authMethod}' is not supported; CIMD v1 is public-only (none).");
        }

        // ── redirect_uris: required, each https-or-loopback, exact-match ──
        var redirectUris = GetStringArray(root, "redirect_uris");
        if (redirectUris.Count == 0)
            return Invalid("document is missing the required redirect_uris.");
        foreach (var uri in redirectUris)
        {
            if (!IsAllowedRedirectUri(uri))
                return Invalid($"redirect_uri '{uri}' is invalid (https URIs or http loopback only).");
        }

        // ── grant_types: subset of {authorization_code, refresh_token} ────
        var grantTypes = root.TryGetProperty("grant_types", out _)
            ? GetStringArray(root, "grant_types")
            : new List<string> { "authorization_code" };
        if (grantTypes.Count == 0)
            grantTypes = new List<string> { "authorization_code" };
        foreach (var grant in grantTypes)
        {
            if (!AllowedGrantTypes.Contains(grant))
                return Invalid($"grant_type '{grant}' is not allowed (authorization_code, refresh_token only).");
        }
        if (!grantTypes.Contains("authorization_code"))
            return Invalid("grant_types must include authorization_code.");

        // ── response_types: subset of {code} ──────────────────────────────
        var responseTypes = root.TryGetProperty("response_types", out _)
            ? GetStringArray(root, "response_types")
            : new List<string> { "code" };
        foreach (var rt in responseTypes)
        {
            if (!AllowedResponseTypes.Contains(rt))
                return Invalid($"response_type '{rt}' is not allowed (code only).");
        }

        // ── scope (optional) + client_name (optional, display only) ───────
        var scopes = ParseScope(TryGetString(root, "scope", out var scope) ? scope : null);
        TryGetString(root, "client_name", out var clientName);

        return new CimdValidationResult.Valid(new CimdMetadata
        {
            ClientId = docClientId,
            ClientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName!.Trim(),
            RedirectUris = redirectUris.Distinct(StringComparer.Ordinal).ToList(),
            GrantTypes = grantTypes.Distinct(StringComparer.Ordinal).ToList(),
            Scopes = scopes,
        });
    }

    /// <summary>RFC 8252 §7.3 + MCP: HTTPS anywhere, HTTP on literal loopback
    /// only. Mirrors the DCR redirect policy so CIMD and DCR agree on what a
    /// valid native/loopback redirect looks like.</summary>
    private static bool IsAllowedRedirectUri(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;

        if (uri.Scheme == Uri.UriSchemeHttps) return true;

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var host = uri.Host;
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "[::1]"
                || host == "::1";
        }

        return false;
    }

    private static List<string> ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return value is not null;
    }

    private static List<string> GetStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static CimdValidationResult.Invalid Invalid(string reason) => new(reason);
}
