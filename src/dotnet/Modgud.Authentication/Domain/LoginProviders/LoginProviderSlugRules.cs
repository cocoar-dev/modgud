using System.Text.RegularExpressions;

namespace Modgud.Authentication.Domain.LoginProviders;

/// <summary>
/// Rules for validating <see cref="LoginProvider.Slug"/> values. Pure, statically
/// usable, and the single source of truth — the create command and any UI form
/// validation should call into here so the rules don't drift.
///
/// <para>A valid slug:</para>
/// <list type="bullet">
///   <item>is 3 to 64 characters total</item>
///   <item>starts with a lowercase letter</item>
///   <item>ends with a lowercase letter or digit</item>
///   <item>contains only lowercase letters, digits, and hyphens</item>
/// </list>
///
/// <para>
/// The slug is the user-facing, recreate-stable identifier in provider URLs:
/// <c>/signin-oidc/{slug}</c> (OIDC callback) and
/// <c>/saml/{slug}/{sp-metadata|login|acs}</c> (SAML SP surface). It replaces the
/// aggregate Guid in those URLs so that deleting + recreating a provider can keep
/// the same URLs (the admin doesn't have to re-paste them into the upstream IdP).
/// </para>
/// <para>
/// <b>No reserved-word list.</b> Unlike <c>RealmSlugRules</c>, no slug value can
/// collide with a system path here: the SAML routes always carry a literal action
/// segment after the slug (<c>/saml/{slug}/login</c>) and the OIDC callback slug is
/// the final segment under a dedicated prefix (<c>/signin-oidc/{slug}</c>) with no
/// sibling literal route. The seeded Internal provider's <c>internal</c> slug is
/// protected by per-realm uniqueness, not a reserved list.
/// </para>
/// <para>
/// <b>Uniqueness is per-realm</b> and enforced at command time, not here — slugs are
/// only unique within a realm; cross-realm collisions are fine because the host
/// resolves the realm before any provider URL is matched.
/// </para>
/// </summary>
public static partial class LoginProviderSlugRules
{
    /// <summary>The slug of the seeded built-in Internal login provider.</summary>
    public const string InternalSlug = "internal";

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.Compiled)]
    private static partial Regex SlugFormatRegex();

    /// <summary>True if <paramref name="slug"/> matches the format rules. Whitespace and null fail.</summary>
    public static bool IsValidFormat(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugFormatRegex().IsMatch(slug);
}
