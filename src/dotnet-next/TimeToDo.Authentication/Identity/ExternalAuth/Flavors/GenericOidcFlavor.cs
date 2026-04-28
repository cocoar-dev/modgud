using System.Text.Json;
using TimeToDo.Authentication.Domain.ExternalAuth;

namespace TimeToDo.Authentication.Identity.ExternalAuth.Flavors;

/// <summary>
/// Fallback flavor for any OIDC-conforming provider that does not have a
/// dedicated flavor class. The admin supplies a discovery URL and everything
/// else derives from there.
/// </summary>
public class GenericOidcFlavor : IIdentityProviderFlavor
{
    public string Key => IdpFlavor.GenericOidc;
    public string DisplayName => "Generic OIDC";
    public string DefaultIconName => "key-round";

    public IReadOnlyList<string> DefaultScopes { get; } = ["openid", "profile", "email"];

    public string DefaultUserUpdateScript => """
        // Default user-property mapping. Returned object patches the TimeToDo
        // user record (Firstname/Lastname/Email/Acronym). `undefined` = skip,
        // `null` = clear. Adjust to your IdP's claim shape.
        (claims) => ({
          firstname: claims.given_name?.trim(),
          lastname: claims.family_name?.trim(),
          email: claims.email,
          acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
        })
        """;

    public bool DefaultStoreRawClaims => true;

    public IReadOnlyList<FlavorConfigField> ConfigSchema { get; } =
    [
        new FlavorConfigField(
            Key: "MetadataUri",
            Type: FlavorConfigFieldType.Url,
            Label: "Discovery URL",
            Required: true,
            HelpText: "OpenID Connect discovery endpoint (well-known config URL).",
            Placeholder: "https://your-idp.example.com/.well-known/openid-configuration"),
    ];

    public OidcEndpoints DeriveEndpoints(JsonDocument? flavorData)
    {
        if (flavorData is null)
            throw new ArgumentException("Generic OIDC flavor requires FlavorData with MetadataUri.", nameof(flavorData));

        if (!flavorData.RootElement.TryGetProperty("MetadataUri", out var metadataElement)
            || metadataElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(metadataElement.GetString()))
        {
            throw new ArgumentException(
                "Generic OIDC flavor requires FlavorData.MetadataUri (non-empty URL).",
                nameof(flavorData));
        }

        var metadataUri = metadataElement.GetString()!;

        // Authority is the metadata URI minus the well-known suffix — a common
        // convention. Handlers that prefer metadata-driven discovery will use
        // MetadataUri directly and ignore this.
        var authority = metadataUri.EndsWith("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase)
            ? metadataUri[..^"/.well-known/openid-configuration".Length]
            : metadataUri;

        return new OidcEndpoints(
            Authority: authority,
            MetadataUri: metadataUri);
    }
}
