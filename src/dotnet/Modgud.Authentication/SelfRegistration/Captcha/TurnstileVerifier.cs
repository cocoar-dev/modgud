using System.Net.Http.Json;
using Modgud.Domain.Realms;
using Microsoft.Extensions.Logging;

namespace Modgud.Authentication.SelfRegistration.Captcha;

/// <summary>
/// Resolves the secret key to use for a given realm's captcha verify
/// call. Fallback chain: per-realm encrypted secret → Cocoar-default
/// system-config secret → null (verifier rejects).
/// </summary>
public interface ITurnstileSecretResolver
{
    string? ResolveSecret(SelfRegistrationSettings? realmSettings);
    string? ResolveSiteKey(SelfRegistrationSettings? realmSettings);
}

/// <summary>
/// Verifies Cloudflare Turnstile widget tokens against the
/// <c>siteverify</c> endpoint. Tokens are single-use, short-lived, and
/// tied to the site-key they were issued for — we don't try to be
/// clever, just round-trip the token + resolved secret and trust
/// Cloudflare's response.
///
/// <para>Returns <see cref="CaptchaResult.Skipped"/> when the realm has
/// <c>CaptchaEnabled=false</c> (or no settings at all). Callers should
/// treat skipped as success — the operator opted out of captcha
/// explicitly. Returns <see cref="CaptchaResult.Failed"/> only on
/// active rejection by Cloudflare or missing/invalid token.</para>
/// </summary>
public sealed class TurnstileVerifier
{
    private const string SiteVerifyEndpoint =
        "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ITurnstileSecretResolver _resolver;
    private readonly ILogger<TurnstileVerifier> _logger;

    public TurnstileVerifier(
        IHttpClientFactory httpFactory,
        ITurnstileSecretResolver resolver,
        ILogger<TurnstileVerifier> logger)
    {
        _httpFactory = httpFactory;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<CaptchaResult> VerifyAsync(
        SelfRegistrationSettings? realmSettings,
        string? token,
        string? remoteIp,
        CancellationToken ct)
    {
        if (realmSettings is null || !realmSettings.CaptchaEnabled)
            return CaptchaResult.Skipped;

        var secret = _resolver.ResolveSecret(realmSettings);
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Settings inconsistency — captcha-enabled but no resolvable
            // secret. PATCH validation should prevent this from being
            // saved; if we reach here, treat as Failed so we don't
            // silently let bots through. A WARNING-level log surfaces
            // the misconfiguration so an admin can fix it.
            _logger.LogWarning(
                "Realm has CaptchaEnabled=true but no resolvable Turnstile secret. Rejecting registration as a safety default.");
            return CaptchaResult.Failed;
        }

        if (string.IsNullOrWhiteSpace(token))
            return CaptchaResult.Failed;

        var http = _httpFactory.CreateClient(nameof(TurnstileVerifier));
        var payload = new Dictionary<string, string?>
        {
            ["secret"] = secret,
            ["response"] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
            payload["remoteip"] = remoteIp;

        try
        {
            using var response = await http.PostAsync(
                SiteVerifyEndpoint,
                new FormUrlEncodedContent(payload!),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Turnstile siteverify returned non-success status {StatusCode}", (int)response.StatusCode);
                return CaptchaResult.Failed;
            }

            var body = await response.Content.ReadFromJsonAsync<TurnstileResponse>(cancellationToken: ct);
            if (body is null || !body.Success)
            {
                _logger.LogInformation(
                    "Turnstile verification rejected by Cloudflare: error_codes={ErrorCodes}",
                    body?.ErrorCodes is { Length: > 0 } errs ? string.Join(",", errs) : "(none)");
                return CaptchaResult.Failed;
            }

            return CaptchaResult.Verified;
        }
        catch (Exception ex)
        {
            // Network blip / DNS hiccup → treat as failure. Better to
            // reject a legit registration (user can retry) than let a
            // bot through. CaptchaResult.Failed is the safe default.
            _logger.LogError(ex, "Turnstile verification call threw — treating as Failed");
            return CaptchaResult.Failed;
        }
    }

    private sealed record TurnstileResponse(
        bool Success,
        string[]? ErrorCodes,
        string? Hostname,
        string? Action);
}

public enum CaptchaResult
{
    /// <summary>Realm explicitly opted out of captcha — no verify call
    /// was made. Treat as success for registration flow purposes.</summary>
    Skipped,

    /// <summary>Cloudflare confirmed the token is valid for this
    /// site-key.</summary>
    Verified,

    /// <summary>Cloudflare rejected the token, the token was missing,
    /// or we couldn't reach Cloudflare. Block the registration.</summary>
    Failed,
}
