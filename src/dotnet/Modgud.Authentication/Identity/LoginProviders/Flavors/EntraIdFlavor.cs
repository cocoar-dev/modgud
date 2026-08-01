using System.Text.Json;
using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Identity.LoginProviders.Flavors;

/// <summary>
/// Microsoft Entra ID (formerly Azure AD). Endpoints derive from the TenantId:
/// <c>https://login.microsoftonline.com/{TenantId}/v2.0</c> as authority with
/// auto-discovery via the standard well-known path.
/// <para>
/// Entra's claim shape has a few quirks: <c>groups</c> contains object-IDs
/// (GUIDs), app-roles come through a <c>roles</c> claim, and custom attributes
/// arrive as <c>extension_*</c>. The default transform script handles the
/// standard fields and leaves the rest to the admin to pull in as needed.
/// </para>
/// </summary>
public class EntraIdFlavor : ILoginProviderFlavor
{
    public string Key => LoginProviderFlavor.EntraId;
    public string DisplayName => "Microsoft Entra ID";
    public string DefaultIconName => "microsoft";

    public IReadOnlyList<string> DefaultScopes { get; } = ["openid", "profile", "email"];

    public string DefaultUserUpdateScript => """
        // Entra ID → Modgud user patch. Returned object updates Firstname/
        // Lastname/Email/Acronym on the linked user; `undefined` = skip,
        // `null` = clear.
        //
        // Notes on Entra's claim shape:
        //   - 'given_name' / 'family_name' are standard OIDC name fields.
        //   - 'email' is preferred; fallback to 'preferred_username' (UPN).
        //   - Custom attributes arrive as 'extension_<appId>_<attrName>'.
        (claims) => ({
          firstname: claims.given_name?.trim(),
          lastname: claims.family_name?.trim(),
          email: claims.email ?? claims.preferred_username,
          acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
        })
        """;

    public bool DefaultStoreRawClaims => true;

    public IReadOnlyList<FlavorConfigField> ConfigSchema { get; } =
    [
        new FlavorConfigField(
            Key: "TenantId",
            Type: FlavorConfigFieldType.String,
            Label: "Tenant ID",
            Required: true,
            HelpText: "Entra tenant GUID, verified domain, or audience alias ('common', 'organizations', 'consumers').",
            Placeholder: "contoso.onmicrosoft.com"),
        .. OidcAdvancedConfigFields.All,
    ];

    public OidcEndpoints DeriveEndpoints(JsonDocument? flavorData)
    {
        if (flavorData is null)
            throw new ArgumentException("Entra ID flavor requires FlavorData with TenantId.", nameof(flavorData));

        if (!flavorData.RootElement.TryGetProperty("TenantId", out var tenantIdElement)
            || tenantIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tenantIdElement.GetString()))
        {
            throw new ArgumentException(
                "Entra ID flavor requires FlavorData.TenantId (non-empty string).",
                nameof(flavorData));
        }

        var tenantId = tenantIdElement.GetString()!;
        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        return new OidcEndpoints(
            Authority: authority,
            MetadataUri: $"{authority}/.well-known/openid-configuration");
    }
}
