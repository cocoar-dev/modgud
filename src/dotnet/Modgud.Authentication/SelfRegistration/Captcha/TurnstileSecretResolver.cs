using Modgud.Domain.Realms;

namespace Modgud.Authentication.SelfRegistration.Captcha;

/// <summary>
/// Default resolver: per-realm encrypted secret first, then a
/// system-default delegate (typically wired to
/// <c>TurnstileSettings.SecretKey</c> via Cocoar.Configuration in
/// Program.cs). Both null = no captcha can run; the verifier rejects.
///
/// <para>The system-default lives behind a delegate so this class
/// stays in the Authentication slice and doesn't need to know about
/// the Api project's config type. Program.cs wires
/// <see cref="SystemDefaultSecret"/> /
/// <see cref="SystemDefaultSiteKey"/> at startup; both return null
/// when not configured.</para>
/// </summary>
public sealed class TurnstileSecretResolver : ITurnstileSecretResolver
{
    private readonly CaptchaSecretStore _store;

    public TurnstileSecretResolver(CaptchaSecretStore store)
    {
        _store = store;
    }

    /// <summary>System-default secret resolver. Wire-up in Program.cs reads
    /// from <c>TurnstileSettings</c>. Returns null when not configured.</summary>
    public Func<string?> SystemDefaultSecret { get; set; } = static () => null;

    /// <summary>System-default site-key resolver. Same wiring as
    /// <see cref="SystemDefaultSecret"/>.</summary>
    public Func<string?> SystemDefaultSiteKey { get; set; } = static () => null;

    public string? ResolveSecret(SelfRegistrationSettings? realmSettings)
    {
        if (realmSettings is null) return null;

        var perRealm = _store.TryDecrypt(realmSettings.EncryptedCaptchaSecret);
        if (!string.IsNullOrWhiteSpace(perRealm)) return perRealm;

        return SystemDefaultSecret();
    }

    public string? ResolveSiteKey(SelfRegistrationSettings? realmSettings)
    {
        if (realmSettings is null) return null;
        if (!string.IsNullOrWhiteSpace(realmSettings.CaptchaSiteKey)) return realmSettings.CaptchaSiteKey;
        return SystemDefaultSiteKey();
    }
}
