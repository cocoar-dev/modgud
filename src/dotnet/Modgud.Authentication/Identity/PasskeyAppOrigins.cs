using Marten;
using Modgud.Authentication.Applications;
using Modgud.Domain.OAuth.Applications;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Passkeys from native apps whose platform reports no web origin. Android's Credential
/// Manager signs the app's certificate hash (<c>android:apk-key-hash:…</c>); a desktop app
/// may sign an origin with a scheme of its own. Neither can pass the RP-ID host check, so
/// the App that owns the RP ID lists the origins of its native apps
/// (<c>SignIn.PasskeyAppOrigins</c>); this decides whether a presented origin is one of
/// them for the ceremony at hand.
///
/// <para>The list is per App, not per client: a brokered ceremony begun and redeemed by
/// the App's web client for an assertion its native app produced belongs to the same App.
/// The RP-ID hash proves the credential is the RP's; the origin is what proves which app
/// asked, so the match is exact — never "any non-web origin".</para>
/// </summary>
public sealed class PasskeyAppOrigins(IApplicationSettingsResolver settingsResolver)
{
    /// <summary>
    /// The presented origin as a one-element list when it is a native app origin that the
    /// App of <paramref name="clientId"/> lists, and the ceremony runs on that App's passkey
    /// RP ID; otherwise null (the verifier then rejects it as before).
    /// </summary>
    public async Task<string[]?> AcceptedAsync(
        IQuerySession session, string? clientId, string? rpId, string? presentedOrigin, CancellationToken ct = default)
    {
        if (!RealmFido2.IsNativeAppOrigin(presentedOrigin)
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(rpId))
            return null;

        var client = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, ct);
        if (client is not { AppIds.Count: 1 }) return null;

        var effective = await settingsResolver.ResolveAsync(client.AppIds[0], ct);
        if (!string.Equals(effective.PasskeyRpId, rpId, StringComparison.OrdinalIgnoreCase)) return null;

        return effective.PasskeyAppOrigins.Contains(presentedOrigin, StringComparer.Ordinal)
            ? [presentedOrigin!]
            : null;
    }
}
