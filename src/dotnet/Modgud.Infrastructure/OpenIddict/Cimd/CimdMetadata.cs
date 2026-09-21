using System.Text.Json;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Infrastructure.OpenIddict.Cimd;

/// <summary>
/// The validated subset of a CIMD metadata document
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

    /// <summary>The document's <c>application_type</c> (<c>web</c> | <c>native</c>), or
    /// null when omitted — most MCP clients omit it and rely on their loopback redirect
    /// URIs to say "native" (see <c>OAuthApplicationTypes.Effective</c>).</summary>
    public string? ApplicationType { get; init; }

    /// <summary><c>none</c> (public, PKCE) or <c>private_key_jwt</c> (confidential,
    /// authenticates with an assertion signed by a key in <see cref="JwksUri"/> or
    /// <see cref="Jwks"/> — exactly one of the two is set).</summary>
    public string TokenEndpointAuthMethod { get; init; } = "none";

    public string? JwksUri { get; init; }

    /// <summary>The inline key set, already filtered by <see cref="CimdJwks"/>.</summary>
    public string? Jwks { get; init; }
}

/// <summary>Outcome of validating a fetched CIMD document against the
/// <c>client_id</c> URL and Modgud's CIMD policy.</summary>
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
    private const string AuthMethodPrivateKeyJwt = "private_key_jwt";

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
            // A UTF-8 BOM survives the byte→string decode as U+FEFF, which
            // JsonDocument refuses; static hosts do serve files with one.
            using var doc = JsonDocument.Parse(json.TrimStart((char)0xFEFF));
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

        // ── client authentication: none, or private_key_jwt with public keys ─
        // The draft forbids every shared-secret method and any client_secret in
        // the document (a published document cannot keep a secret). A client
        // that can hold a private key authenticates with private_key_jwt, its
        // public keys at jwks_uri or inline in jwks — never both (RFC 7591 §2).
        if (root.TryGetProperty("client_secret", out _))
            return Invalid("document must not contain a client_secret (a published document cannot keep one).");

        var authMethod = TryGetString(root, "token_endpoint_auth_method", out var declaredAuth) && declaredAuth is not null
            ? declaredAuth
            : AuthMethodNone;
        string? jwksUri = null;
        string? jwks = null;
        if (authMethod == AuthMethodPrivateKeyJwt)
        {
            var hasUri = TryGetString(root, "jwks_uri", out var declaredJwksUri) && declaredJwksUri is not null;
            var hasInline = root.TryGetProperty("jwks", out var inlineJwks) && inlineJwks.ValueKind == JsonValueKind.Object;
            if (hasUri == hasInline)
                return Invalid("private_key_jwt needs exactly one of jwks_uri or jwks.");
            if (hasUri)
            {
                if (!IsAllowedJwksUri(declaredJwksUri!))
                    return Invalid("jwks_uri must be an absolute https URL without userinfo or fragment.");
                jwksUri = declaredJwksUri;
            }
            else if (!CimdJwks.TryFilter(inlineJwks.GetRawText(), out jwks, out var jwksError))
            {
                return Invalid($"jwks: {jwksError}");
            }
        }
        else if (authMethod != AuthMethodNone)
        {
            return Invalid($"token_endpoint_auth_method '{authMethod}' is not supported (none or private_key_jwt; shared secrets are forbidden for CIMD).");
        }

        // ── redirect_uris: https or http loopback; the rest is dropped ────
        // Same reasoning as grant_types: the document serves every server, so a
        // URI form Modgud does not accept (a private-use scheme, say) costs the
        // client that one URI, not the whole registration. At least one must
        // survive. Loopback URIs with a port also get their port-less twin.
        var declaredRedirectUris = GetStringArray(root, "redirect_uris");
        if (declaredRedirectUris.Count == 0)
            return Invalid("document is missing the required redirect_uris.");
        var redirectUris = declaredRedirectUris.Where(IsAllowedRedirectUri).ToList();
        if (redirectUris.Count == 0)
            return Invalid("no usable redirect_uri (https URIs or http loopback only).");
        redirectUris = OAuthApplicationTypes.WithPortlessLoopbackTwins(redirectUris);

        // ── grant_types: intersected with {authorization_code, refresh_token} ─
        // A CIMD document is the client's self-description for EVERY
        // authorization server, not an order placed with this one: it lists
        // the grants the client can use (RFC 7591 §2). Grants Modgud does not
        // offer (claude.ai lists jwt-bearer) are dropped, not fatal — the
        // client simply never holds them. authorization_code must survive.
        var grantTypes = root.TryGetProperty("grant_types", out _)
            ? GetStringArray(root, "grant_types")
            : new List<string> { "authorization_code" };
        if (grantTypes.Count == 0)
            grantTypes = new List<string> { "authorization_code" };
        grantTypes = grantTypes.Where(AllowedGrantTypes.Contains).ToList();
        if (!grantTypes.Contains("authorization_code"))
            return Invalid("grant_types must include authorization_code.");

        // ── response_types: must include code; anything else is ignored ───
        var responseTypes = root.TryGetProperty("response_types", out _)
            ? GetStringArray(root, "response_types")
            : new List<string> { "code" };
        if (responseTypes.Count > 0 && !responseTypes.Any(AllowedResponseTypes.Contains))
            return Invalid("response_types must include code.");

        // ── application_type (optional): web | native, nothing else ───────
        string? applicationType = null;
        if (TryGetString(root, "application_type", out var declaredType) && declaredType is not null)
        {
            if (!OAuthApplicationTypes.IsKnown(declaredType))
                return Invalid($"application_type '{declaredType}' is not valid (web or native).");
            applicationType = declaredType;
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
            ApplicationType = applicationType,
            TokenEndpointAuthMethod = authMethod,
            JwksUri = jwksUri,
            Jwks = jwks,
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

    private static bool IsAllowedJwksUri(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

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
