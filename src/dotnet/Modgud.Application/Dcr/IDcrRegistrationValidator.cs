using System.Globalization;
using System.Text;
using Modgud.Application.DTOs.OAuth;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;

namespace Modgud.Application.Dcr;

/// <summary>
/// Pure policy layer for <c>/connect/register</c>. The endpoint handler
/// is plumbing — every reject path lives here so it's unit-testable
/// without spinning up the HTTP pipeline or Marten.
///
/// <para>The validator does NOT touch the database. The endpoint
/// resolves <see cref="DcrSettings"/>, source IP, and the realm id ahead
/// of time and feeds them in; the validator decides allow/deny + the
/// normalised registration payload.</para>
/// </summary>
public interface IDcrRegistrationValidator
{
    /// <summary>
    /// Run the full validation pipeline. Returns <see cref="DcrValidationResult.Allow"/>
    /// with a normalised CreateOAuthClientDto on success, or
    /// <see cref="DcrValidationResult.Reject"/> with an RFC 7591
    /// error-code + human-readable description + machine-readable
    /// rejection reason on failure.
    /// </summary>
    DcrValidationResult Validate(
        DcrRegistrationRequest request,
        DcrSettings settings,
        string sourceIp);
}

public abstract record DcrValidationResult
{
    /// <param name="Normalized">The CreateOAuthClientDto to persist.</param>
    /// <param name="TokenEndpointAuthMethod">The negotiated client-auth method
    /// (<c>none</c> / <c>client_secret_basic</c> / <c>client_secret_post</c>) —
    /// echoed back in the RFC 7591 §3.2.1 response and used by the endpoint to
    /// decide whether to surface the generated <c>client_secret</c>.</param>
    public sealed record Allow(CreateOAuthClientDto Normalized, string TokenEndpointAuthMethod) : DcrValidationResult;
    public sealed record Reject(string ErrorCode, string ErrorDescription, DcrRejectionReason Reason) : DcrValidationResult;
}

/// <summary>
/// Default validator wiring up redirect-uri policy, grant/auth-method
/// whitelist, and client_name spoofing defence. The
/// <see cref="DcrRateLimiter"/> is checked separately by the endpoint
/// before invoking <see cref="Validate"/> — keeping rate-limit state
/// out of the pure validator means unit tests don't need to reset
/// process-wide counters between runs.
/// </summary>
public sealed class DcrRegistrationValidator : IDcrRegistrationValidator
{
    // Per RFC 7591 + MCP guidance + the locked v1 design.
    private static readonly HashSet<string> AllowedGrantTypes = new(StringComparer.Ordinal)
    {
        "authorization_code",
        "refresh_token",
    };

    private static readonly HashSet<string> AllowedResponseTypes = new(StringComparer.Ordinal)
    {
        "code",
    };

    // Public PKCE ("none") stays the default. Secret-based confidential methods
    // are accepted so clients that authenticate at the token endpoint (e.g. the
    // claude.ai MCP connector, which registers confidential) can self-register.
    // private_key_jwt is intentionally NOT accepted via DCR: it needs a
    // registered JWKS, which the v1 /connect/register request shape doesn't
    // carry — such clients must be pre-registered by an admin.
    private const string AuthMethodNone = "none";
    private const string AuthMethodClientSecretBasic = "client_secret_basic";
    private const string AuthMethodClientSecretPost = "client_secret_post";

    private static readonly HashSet<string> AllowedTokenEndpointAuthMethods = new(StringComparer.Ordinal)
    {
        AuthMethodNone,
        AuthMethodClientSecretBasic,
        AuthMethodClientSecretPost,
    };

    private const int ClientNameMaxLength = 80;

    public DcrValidationResult Validate(
        DcrRegistrationRequest request,
        DcrSettings settings,
        string sourceIp)
    {
        _ = sourceIp; // Reserved for future per-IP-aware decisions; rate-limit lives in DcrRateLimiter.
        // ───────── redirect_uris ────────────────────────────────────
        if (request.RedirectUris is null || request.RedirectUris.Count == 0)
        {
            return Reject(DcrErrorCodes.InvalidRedirectUri,
                "redirect_uris is required and must contain at least one entry.",
                DcrRejectionReason.MissingRedirectUri);
        }

        foreach (var uri in request.RedirectUris)
        {
            if (!IsAllowedRedirectUri(uri))
            {
                return Reject(DcrErrorCodes.InvalidRedirectUri,
                    $"redirect_uri '{uri}' is invalid. Allowed forms: https URIs, http://localhost, http://127.0.0.1, http://[::1]. Custom URI schemes (com.example.app://) are not supported in v1.",
                    DcrRejectionReason.InvalidRedirectUri);
            }
        }

        // ───────── token_endpoint_auth_method ───────────────────────
        // Default to "none" if omitted (RFC 7591 leaves the default
        // server-defined; public PKCE is the natural default for a DCR client).
        var authMethod = request.TokenEndpointAuthMethod ?? AuthMethodNone;
        if (!AllowedTokenEndpointAuthMethods.Contains(authMethod))
        {
            return Reject(DcrErrorCodes.InvalidClientMetadata,
                $"token_endpoint_auth_method '{authMethod}' is not supported via DCR. Supported: {string.Join(", ", AllowedTokenEndpointAuthMethods)}. (private_key_jwt requires admin pre-registration with a JWKS.)",
                DcrRejectionReason.InvalidTokenAuthMethod);
        }

        // none → public PKCE client; client_secret_* → confidential client with
        // a server-generated secret (CreateClientAsync mints it; the endpoint
        // returns it once in the RFC 7591 §3.2.1 response).
        var isConfidential = authMethod != AuthMethodNone;

        // ───────── grant_types ──────────────────────────────────────
        var grantTypes = request.GrantTypes is { Count: > 0 }
            ? request.GrantTypes
            : new List<string> { "authorization_code" }; // RFC 7591 §2 default

        foreach (var grant in grantTypes)
        {
            if (!AllowedGrantTypes.Contains(grant))
            {
                return Reject(DcrErrorCodes.InvalidClientMetadata,
                    $"grant_type '{grant}' is not allowed. Allowed: {string.Join(", ", AllowedGrantTypes)}.",
                    DcrRejectionReason.InvalidGrantType);
            }
        }

        // ───────── response_types ───────────────────────────────────
        var responseTypes = request.ResponseTypes is { Count: > 0 }
            ? request.ResponseTypes
            : new List<string> { "code" };

        foreach (var rt in responseTypes)
        {
            if (!AllowedResponseTypes.Contains(rt))
            {
                return Reject(DcrErrorCodes.InvalidClientMetadata,
                    $"response_type '{rt}' is not allowed. Only 'code' is supported (implicit/hybrid flows are out of scope).",
                    DcrRejectionReason.InvalidResponseType);
            }
        }

        // ───────── client_name ──────────────────────────────────────
        var clientName = request.ClientName?.Trim();
        if (string.IsNullOrEmpty(clientName))
        {
            return Reject(DcrErrorCodes.InvalidClientMetadata,
                "client_name is required.",
                DcrRejectionReason.ClientNameMissing);
        }

        if (clientName.Length > ClientNameMaxLength)
        {
            return Reject(DcrErrorCodes.InvalidClientMetadata,
                $"client_name must be {ClientNameMaxLength} characters or fewer.",
                DcrRejectionReason.ClientNameTooLong);
        }

        // NFKC normalisation collapses compatibility-equivalent forms
        // (e.g. zero-width-joiner + character → bare character) so
        // visual lookalikes can't bypass the substring blocklist by
        // inserting invisible glyphs.
        var normalisedName = clientName.Normalize(NormalizationForm.FormKC);

        if (!IsLatin1Only(normalisedName))
        {
            return Reject(DcrErrorCodes.InvalidClientMetadata,
                "client_name must use ASCII or Latin-1 characters only (after NFKC normalisation).",
                DcrRejectionReason.ClientNameNonLatin1);
        }

        if (settings.ReservedNames is { Length: > 0 })
        {
            var lowered = normalisedName.ToLowerInvariant();
            foreach (var reserved in settings.ReservedNames)
            {
                if (string.IsNullOrEmpty(reserved)) continue;
                var normalisedReserved = reserved
                    .Normalize(NormalizationForm.FormKC)
                    .ToLowerInvariant();
                if (lowered.Contains(normalisedReserved))
                {
                    return Reject(DcrErrorCodes.InvalidClientMetadata,
                        "client_name conflicts with a name reserved by this realm.",
                        DcrRejectionReason.ClientNameReservedName);
                }
            }
        }

        // ───────── normalisation ────────────────────────────────────
        var requestedScopes = ParseScope(request.Scope);

        var normalized = new CreateOAuthClientDto
        {
            ClientId = "dcr-" + Guid.NewGuid().ToString("N"),
            DisplayName = normalisedName,
            ClientType = isConfidential ? OAuthClientTypes.Confidential : OAuthClientTypes.Public,
            ConsentType = OAuthConsentTypes.Explicit, // DCR clients always go through consent
            RedirectUris = request.RedirectUris.ToList(),
            PostLogoutRedirectUris = new List<string>(),
            AllowedGrantTypes = grantTypes.ToList(),
            Scopes = requestedScopes,
            Enabled = true,
            RequireConsent = true,
            AllowRememberConsent = false,                  // DCR consent is per-session — re-affirm each time
            // Confidential → CreateClientAsync generates + persists (hashed) a
            // secret because ClientSecret is left null here. Public → no secret.
            RequireClientSecret = isConfidential,
            EnableLocalLogin = true,
            AllowAccessTokensViaBrowser = false,
            // JWT is the idiomatic format for MCP / agent flows: the
            // resource server validates the token via JWKS rather than
            // making an introspection round-trip back to the IdP. The
            // tighter DCR access-token lifetime caps the staleness
            // window that this self-validation trades for.
            AccessTokenType = AccessTokenType.Jwt,
            AccessTokenLifetime = (int)settings.AccessTokenLifetime.TotalSeconds,
            SlidingRefreshTokenLifetime = (int)settings.RefreshTokenLifetime.TotalSeconds,
            Claims = new List<OAuthClientClaimDto>(),
            Roles = new List<string>(),
            AllowedCorsOrigins = new List<string>(),
        };

        return new DcrValidationResult.Allow(normalized, authMethod);
    }

    /// <summary>RFC 8252 §7.3 + MCP spec: HTTPS anywhere, HTTP on
    /// loopback only (literal 127.0.0.1, ::1, or localhost — never a
    /// regular hostname).</summary>
    private static bool IsAllowedRedirectUri(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false; // RFC 6749 §3.1.2

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

    /// <summary>Reject anything outside the Latin-1 supplement range
    /// (U+0000–U+00FF) after NFKC. Cuts the bulk of confusable-attack
    /// surface (Cyrillic А vs Latin A, fullwidth glyphs, etc.) with no
    /// ICU dependency. Whitespace and most punctuation pass through.</summary>
    private static bool IsLatin1Only(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x00FF) return false;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Control && c != '\t')
                return false;
        }
        return true;
    }

    private static List<string> ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static DcrValidationResult.Reject Reject(string code, string description, DcrRejectionReason reason)
        => new(code, description, reason);
}
