namespace Modgud.Api;

/// <summary>
/// Cocoar-wide default Cloudflare Turnstile keys, used as fallback when a
/// realm has <c>CaptchaEnabled=true</c> but no per-realm overrides set.
/// All fields optional — leaving them null means "no system-wide default"
/// (tenants with their own keys still work, tenants without keys fail
/// PATCH-validation when they try to enable captcha).
///
/// <para>Bound from <c>data/configuration.json</c> / env-vars under the
/// <c>Turnstile</c> section, same pattern as <c>EmailConfiguration</c>
/// / <c>MagicLinkConfiguration</c>.</para>
///
/// <para>Setup: create a free account at <c>dash.cloudflare.com</c>,
/// add a Turnstile site for the IdP's host, copy site-key + secret-key
/// here. Cloudflare-free is unlimited and privacy-friendly; tenants who
/// want their own analytics / data-residency override per-realm.</para>
/// </summary>
public class TurnstileSettings
{
    /// <summary>Public site-key (starts with <c>0x4A...</c>). Goes to the
    /// SPA so the Turnstile widget can mount. Safe to expose — Cloudflare
    /// rate-limits and signs requests by site-key origin.</summary>
    public string? SiteKey { get; set; }

    /// <summary>Server-side secret-key. The IdP uses this to call
    /// <c>challenges.cloudflare.com/turnstile/v0/siteverify</c>. Never
    /// surfaced via any API. Encrypted-at-rest only at the per-realm
    /// override level — system-default is read from configuration files
    /// / env-vars (typically host-secret store).</summary>
    public string? SecretKey { get; set; }
}
