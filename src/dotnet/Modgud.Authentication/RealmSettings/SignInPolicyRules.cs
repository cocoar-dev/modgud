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

    /// <summary>ADR 0025 — the realm policy in force: the saved one, or the one derived from
    /// the retired deployment settings while the realm has never saved it. The same derivation
    /// <c>ApplicationSettingsResolver</c> applies at runtime.</summary>
    public static SignInPolicy InForce(Modgud.Domain.RealmSettings.RealmSettings? realm, IAuthSettings? authSettings)
    {
        if (realm?.SignIn is not null) return realm.SignIn;
        if (authSettings is null) return SignInPolicy.Defaults;
#pragma warning disable CS0618 // the retired deployment settings seed the derived policy
        return SignInPolicy.FromLegacy(authSettings.AuthenticationMinimumLevel, authSettings.TwoFactorGracePeriodDays,
            emailCodeEnabled: realm?.NativeGrants?.Enabled ?? false);
#pragma warning restore CS0618
    }
}
