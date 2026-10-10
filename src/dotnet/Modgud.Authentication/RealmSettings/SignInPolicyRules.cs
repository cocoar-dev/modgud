using System.Text.RegularExpressions;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.RealmSettings;

/// <summary>
/// ADR 0025 — shared parsing and validation of the sign-in policy for the realm
/// (<c>RealmSettingsService</c>) and the per-App override (<c>ApplicationSettingsService</c>).
/// </summary>
internal static partial class SignInPolicyRules
{
    // A bare host name: no scheme, port or path, lower-case, at least one dot or "localhost".
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    public static ErrorOr<SignInLevel?> ParseLevel(string field, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (SignInLevel?)null;
        if (!Enum.TryParse<SignInLevel>(raw, ignoreCase: true, out var v) || !Enum.IsDefined(v))
            return Error.Validation($"SignIn.Invalid{field}", $"{field} must be Single or Multi.");
        return (SignInLevel?)v;
    }

    public static ErrorOr<OwnFactorNotOffered?> ParseOwnFactor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (OwnFactorNotOffered?)null;
        if (!Enum.TryParse<OwnFactorNotOffered>(raw, ignoreCase: true, out var v) || !Enum.IsDefined(v))
            return Error.Validation("SignIn.InvalidOwnFactorNotOffered",
                "OwnFactorNotOffered must be Ignore or RequireViaBrowser.");
        return (OwnFactorNotOffered?)v;
    }

    public static Error? ValidateGraceDays(int? days) =>
        days is < 0 or > 365
            ? Error.Validation("SignIn.InvalidSetupGraceDays", "SetupGraceDays must be between 0 and 365.")
            : null;

    /// <summary>Null/blank passes (no RP ID). Returns the trimmed value via <paramref name="normalized"/>.</summary>
    public static Error? ValidatePasskeyRpId(string? raw, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (normalized is null) return null;
        if (!HostPattern().IsMatch(normalized) || (normalized != "localhost" && !normalized.Contains('.')))
            return Error.Validation("SignIn.InvalidPasskeyRpId",
                "PasskeyRpId must be a bare, lower-case host name (no scheme, port or path), e.g. 'app.example.com'.");
        return null;
    }

    /// <summary>Normalises the App's native app origins (see
    /// <see cref="Modgud.Authentication.Identity.RealmFido2.NormalizeAppOrigin"/>);
    /// null/empty passes as null. Duplicates collapse.</summary>
    public static Error? ValidateAppOrigins(string[]? raw, out string[]? normalized)
    {
        normalized = null;
        if (raw is null) return null;
        var result = new List<string>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (Modgud.Authentication.Identity.RealmFido2.NormalizeAppOrigin(entry) is not { } origin)
                return Error.Validation("SignIn.InvalidPasskeyAppOrigin",
                    $"'{entry.Trim()}' is not a native app origin. Use the origin the app signs (e.g. app://notes; "
                    + "not http, https or file), or for Android the SHA-256 fingerprint of the signing certificate "
                    + "(e.g. 37:12:A2:…) or android:apk-key-hash:<base64url hash>.");
            if (!result.Contains(origin, StringComparer.Ordinal)) result.Add(origin);
        }
        normalized = result.Count == 0 ? null : [.. result];
        return null;
    }

    /// <summary>
    /// Rejects a policy nobody could satisfy: a Multi level (minimum, or the realm-only
    /// administration level) with no way to reach it, or no way to sign in at all.
    /// An external login provider counts as a way in and as a way to Multi (its IdP may
    /// assert MFA, <c>HasFederatedMfa</c>), so only the no-provider case can fail.
    /// <paramref name="providerIds"/>: the App's explicit allow-list, or null for "every
    /// enabled external provider of the realm".
    /// </summary>
    public static async Task<Error?> CheckSatisfiableAsync(
        IQuerySession session, SignInPolicy policy, bool includeAdministration,
        IReadOnlyCollection<Guid>? providerIds, string subject, CancellationToken ct)
    {
        var needsMulti = policy.MinimumLevel == SignInLevel.Multi
                         || (includeAdministration && policy.AdministrationMinimumLevel == SignInLevel.Multi);
        var hasFirstFactor = policy.Password || policy.EmailCode || policy.Passkey;
        if (hasFirstFactor && (!needsMulti || policy.CanReachMulti)) return null;

        var hasExternal = providerIds is not null
            ? providerIds.Count > 0
            : await session.Query<LoginProvider>()
                .AnyAsync(p => p.Enabled && !p.IsDeleted && p.Type != LoginProviderType.Internal, ct);
        if (hasExternal) return null;

        if (!hasFirstFactor)
            return Error.Validation("SignIn.NoFirstFactor",
                $"{subject}: no sign-in method is offered (password, e-mail code and passkey are all off) and no external login provider is available.");

        return Error.Validation("SignIn.UnreachableMinimumLevel",
            $"{subject}: the minimum level is Multi, but the offered methods cannot reach it. " +
            "Multi needs a passkey, an authenticator-app code after a password or e-mail code, " +
            "or an e-mail code after a password - or an external login provider.");
    }

    /// <summary>ADR 0025 — the realm policy in force: the saved one, or, while the realm has
    /// never saved it, <see cref="SignInPolicy.ForUnsavedRealm"/>. The same rule
    /// <c>ApplicationSettingsResolver</c> applies at runtime.</summary>
    public static async Task<SignInPolicy> InForceAsync(
        IQuerySession session, Modgud.Domain.RealmSettings.RealmSettings? realm, CancellationToken ct = default) =>
        realm?.SignIn ?? SignInPolicy.ForUnsavedRealm(await AnyNativeGrantsAsync(session, realm, ct));

    /// <summary>Whether native grants are switched on for the realm or any of its Apps. The
    /// derived floor offers the e-mail code then: the realm must offer every method an App
    /// offers (amendment A), and native grants were the gate for e-mail-code sign-in.</summary>
    public static async Task<bool> AnyNativeGrantsAsync(
        IQuerySession session, Modgud.Domain.RealmSettings.RealmSettings? realm, CancellationToken ct = default) =>
        realm?.NativeGrants?.Enabled == true
        || await session.Query<Modgud.Domain.Applications.ApplicationSettings>()
            .AnyAsync(a => a.NativeGrants != null && a.NativeGrants.Enabled == true, ct);
}
