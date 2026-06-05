using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Modgud.Domain.Realms;

namespace Modgud.Authentication;

/// <summary>
/// Single source of truth for a realm's public base URL — the origin every
/// outbound user-facing link is built against (magic-link, password-reset,
/// email-verify, bootstrap-invite, login-provider callbacks).
///
/// <para>The base URL is keyed off the realm's <see cref="Realm.PrimaryDomain"/>
/// (its designated canonical host), NOT a single global config value, so each
/// realm's links always point at its own domain. The dev/prod split mirrors
/// the previous convention:
/// <list type="bullet">
///   <item>Production → <c>https://{PrimaryDomain}</c> (the reverse proxy
///   fronts the SPA on 443, no port suffix).</item>
///   <item>Development → <c>http://{PrimaryDomain}:4300</c> (the Vue SPA dev
///   server port; <c>localhost</c> / <c>*.localhost</c> are browser
///   secure-contexts so passkeys work too).</item>
/// </list></para>
/// </summary>
public static class RealmPublicUrl
{
    /// <summary>The SPA dev-server port (Vite). Outbound links in Development
    /// point here, not at the API port.</summary>
    public const int DevSpaPort = 4300;

    /// <summary>
    /// Returns the realm's public base URL (no trailing slash), keyed off
    /// <see cref="Realm.PrimaryDomain"/>. Production = <c>https://{host}</c>;
    /// Development = <c>http://{host}:4300</c>.
    /// </summary>
    public static string RealmPublicBaseUrl(Realm realm, IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(env);
        return BuildBaseUrl(realm.PrimaryDomain, env.IsDevelopment());
    }

    /// <summary>
    /// <see cref="IHostEnvironment"/> overload — same result. Some
    /// non-HTTP-bound services (e.g. <c>SelfRegistrationService</c>) only have
    /// an <see cref="IHostEnvironment"/> injected.
    /// </summary>
    public static string RealmPublicBaseUrl(Realm realm, IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(env);
        return BuildBaseUrl(realm.PrimaryDomain, env.IsDevelopment());
    }

    private static string BuildBaseUrl(string primaryDomain, bool isDevelopment)
    {
        var host = (primaryDomain ?? string.Empty).Trim();
        // Defense in depth: a realm must always have a PrimaryDomain (enforced
        // at create/update/adopt + backfilled at boot). If one is somehow empty,
        // fail loudly here rather than emit a host-less "https://" link that
        // would silently reach a user. The boot backfill /
        // `recover realm-set-primary-domain` is the remediation.
        if (host.Length == 0)
            throw new InvalidOperationException(
                "Realm has no PrimaryDomain — cannot build a public URL. Every realm must have a primary domain.");
        return isDevelopment
            ? $"http://{host}:{DevSpaPort}"
            : $"https://{host}";
    }
}
