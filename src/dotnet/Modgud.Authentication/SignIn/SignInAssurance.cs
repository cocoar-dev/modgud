using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.SignIn;

/// <summary>ADR 0025 — the factors a sign-in can prove. Internal names; tokens carry the
/// RFC 8176 equivalents (<see cref="SignInAssurance.ToAmr"/>).</summary>
public static class SignInMethods
{
    /// <summary>Password (knowledge).</summary>
    public const string Password = "pwd";

    /// <summary>A code or link sent to the e-mail address (mailbox possession). E-mail code,
    /// magic link and the e-mail second factor are all this one factor.</summary>
    public const string Email = "email";

    /// <summary>Authenticator-app code (possession of the TOTP secret).</summary>
    public const string Totp = "totp";

    /// <summary>User-verified passkey — multi-factor on its own.</summary>
    public const string Passkey = "passkey";

    /// <summary>A federated sign-in through an external identity provider.</summary>
    public const string External = "external";

    /// <summary>A federated sign-in whose provider asserted MFA (RFC 8176 <c>amr</c>).</summary>
    public const string ExternalMfa = "external_mfa";
}

/// <summary>
/// ADR 0025 — how a session records what its sign-in proved. Each proven factor is one
/// <see cref="FactorClaimType"/> claim <c>"{method}:{unix seconds}"</c> on the application
/// cookie; the level is derived from them, never stored separately, so it cannot disagree
/// with the factors.
///
/// <para>Sign-in endpoints <see cref="Declare"/> the factors they proved before they issue
/// the cookie; <c>BrowserSessionCookieEvents.SigningIn</c> stamps them, unioned with the
/// factors of the same user's current session — that union is the step-up: a session's
/// level only goes up until logout.</para>
/// </summary>
public static class SignInAssurance
{
    public const string FactorClaimType = "modgud.signin.factor";

    /// <summary><c>acr</c> values (ADR 0025 "Settled details").</summary>
    public const string AcrSingle = "urn:modgud:acr:single";
    public const string AcrMulti = "urn:modgud:acr:multi";

    private const string DeclaredItem = "modgud.signin.declared";

    /// <summary>RFC 8176 <c>amr</c> values a federated provider uses to assert MFA.</summary>
    public static readonly IReadOnlySet<string> FederatedMfaAmrValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mfa", "otp", "fido", "hwk", "swk", "mca", "pop" };

    private static readonly HashSet<string> SingleFactorKinds = new(StringComparer.Ordinal)
    {
        SignInMethods.Password, SignInMethods.Email, SignInMethods.Totp, SignInMethods.External,
    };

    /// <summary>Record the factors the current request proved, for the cookie about to be
    /// issued. Factors proven earlier in the same flow (the first factor of a two-step
    /// sign-in) are passed with their original time.</summary>
    public static void Declare(HttpContext http, IReadOnlyDictionary<string, DateTimeOffset> factors) =>
        http.Items[DeclaredItem] = new Dictionary<string, DateTimeOffset>(factors, StringComparer.Ordinal);

    public static void Declare(HttpContext http, params string[] methods)
    {
        var now = DateTimeOffset.UtcNow;
        Declare(http, methods.Distinct(StringComparer.Ordinal).ToDictionary(m => m, _ => now, StringComparer.Ordinal));
    }

    internal static IReadOnlyDictionary<string, DateTimeOffset>? TakeDeclared(HttpContext http)
    {
        if (!http.Items.Remove(DeclaredItem, out var raw)) return null;
        return raw as IReadOnlyDictionary<string, DateTimeOffset>;
    }

    /// <summary>The factors a principal carries (cookie, partial-sign-in cookie or token).</summary>
    public static Dictionary<string, DateTimeOffset> ReadFactors(ClaimsPrincipal? principal)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (principal is null) return result;
        foreach (var claim in principal.FindAll(FactorClaimType))
        {
            var separator = claim.Value.LastIndexOf(':');
            if (separator <= 0) continue;
            var method = claim.Value[..separator];
            if (!long.TryParse(claim.Value[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
                continue;
            var at = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (!result.TryGetValue(method, out var existing) || at > existing)
                result[method] = at;
        }
        return result;
    }

    /// <summary>Replace the factor claims on <paramref name="identity"/>.</summary>
    public static void Stamp(ClaimsIdentity identity, IReadOnlyDictionary<string, DateTimeOffset> factors)
    {
        foreach (var old in identity.FindAll(FactorClaimType).ToList())
            identity.RemoveClaim(old);
        foreach (var (method, at) in factors)
            identity.AddClaim(new Claim(FactorClaimType,
                $"{method}:{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Union of two factor sets; a factor proven in both keeps the later time.</summary>
    public static Dictionary<string, DateTimeOffset> Union(
        IReadOnlyDictionary<string, DateTimeOffset> a, IReadOnlyDictionary<string, DateTimeOffset> b)
    {
        var result = new Dictionary<string, DateTimeOffset>(a, StringComparer.Ordinal);
        foreach (var (method, at) in b)
            if (!result.TryGetValue(method, out var existing) || at > existing)
                result[method] = at;
        return result;
    }

    /// <summary>The level a set of factors reaches. <c>multi</c> = a factor that is
    /// multi-factor on its own, or two different single factors. Two codes over the same
    /// channel are the same factor (<see cref="SignInMethods.Email"/>), so they never add up.</summary>
    public static SignInLevel LevelOf(IEnumerable<string> methods)
    {
        var set = methods as IReadOnlyCollection<string> ?? methods.ToList();
        if (set.Contains(SignInMethods.Passkey) || set.Contains(SignInMethods.ExternalMfa))
            return SignInLevel.Multi;
        return set.Count(SingleFactorKinds.Contains) >= 2 ? SignInLevel.Multi : SignInLevel.Single;
    }

    public static SignInLevel LevelOf(ClaimsPrincipal? principal) => LevelOf(ReadFactors(principal).Keys);

    public static string ToAcr(SignInLevel level) => level >= SignInLevel.Multi ? AcrMulti : AcrSingle;

    /// <summary>RFC 8176 <c>amr</c> values for a set of factors.</summary>
    public static IReadOnlyList<string> ToAmr(IEnumerable<string> methods)
    {
        var set = methods.ToHashSet(StringComparer.Ordinal);
        var amr = new List<string>();
        void Add(string value) { if (!amr.Contains(value)) amr.Add(value); }
        if (set.Contains(SignInMethods.Password)) Add("pwd");
        if (set.Contains(SignInMethods.Email) || set.Contains(SignInMethods.Totp)) Add("otp");
        if (set.Contains(SignInMethods.Passkey)) { Add("hwk"); Add("user"); }
        if (set.Contains(SignInMethods.External) || set.Contains(SignInMethods.ExternalMfa)) Add("fed");
        if (LevelOf(set) >= SignInLevel.Multi) Add("mfa");
        return amr;
    }

    /// <summary>The factors a federated sign-in proved: always <see cref="SignInMethods.External"/>,
    /// plus <see cref="SignInMethods.ExternalMfa"/> when the provider asserted MFA.</summary>
    public static string[] ExternalMethods(IEnumerable<string> providerAmr) =>
        providerAmr.Any(FederatedMfaAmrValues.Contains)
            ? [SignInMethods.External, SignInMethods.ExternalMfa]
            : [SignInMethods.External];

    // ── Partial (between first and second factor) sign-in ──

    /// <summary>Issue Identity's partial two-factor cookie for <paramref name="userId"/>,
    /// carrying the first factor(s) already proven, so the second step can record both.
    /// Same principal shape Identity writes itself (the user id in <see cref="ClaimTypes.Name"/>),
    /// so its <c>TwoFactor*SignInAsync</c> completion keeps working.</summary>
    public static Task SignInPartialAsync(
        HttpContext http, Guid userId, IReadOnlyDictionary<string, DateTimeOffset> provenFactors)
    {
        var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString()));
        Stamp(identity, provenFactors);
        return http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>The user id and first factor(s) of the pending partial sign-in, if any.
    /// A partial cookie written by Identity itself (password sign-in) carries no factor
    /// claim; it is a password sign-in by construction.</summary>
    public static async Task<(Guid UserId, Dictionary<string, DateTimeOffset> Factors)?> ReadPartialAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        if (!result.Succeeded || !Guid.TryParse(result.Principal?.FindFirstValue(ClaimTypes.Name), out var userId))
            return null;
        var factors = ReadFactors(result.Principal);
        if (factors.Count == 0)
            factors[SignInMethods.Password] = result.Properties?.IssuedUtc ?? DateTimeOffset.UtcNow;
        return (userId, factors);
    }
}
