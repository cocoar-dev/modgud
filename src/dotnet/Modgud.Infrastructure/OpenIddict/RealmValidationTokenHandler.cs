using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using OpenIddict.Validation;
using static OpenIddict.Validation.OpenIddictValidationEvents;
using static OpenIddict.Validation.OpenIddictValidationHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// The VALIDATION-pipeline counterpart of <see cref="RealmTokenValidationHandler"/>.
/// The server pipeline (userinfo / introspect / token) already restricts incoming
/// access-token validation to the active realm's keys + issuer; but a custom
/// resource endpoint protected by the OpenIddict <em>validation</em> scheme
/// (<c>OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme</c>) runs the
/// SEPARATE validation pipeline, which by default validates the token against the
/// global issuer (<c>Options.Issuer</c>) — so a realm-signed access token (whose
/// <c>iss</c> is the per-realm request BaseUri, ADR-0002 / C3c) is rejected with
/// <c>invalid_token</c> ("issuer not valid", ID2088).
///
/// <para>This handler installs the active realm's verification keys and accepts the
/// realm's issuer on the validation side, so Bearer-protected native endpoints
/// (ADR-0009 passkey enrollment) authenticate realm-signed access tokens. It is the
/// exact mirror of the server handler; the realm is resolved from
/// <see cref="TenantContext.Current"/> (set by RealmMiddleware, which runs before the
/// authentication middleware).</para>
/// </summary>
public sealed class RealmValidationTokenHandler : IOpenIddictValidationHandler<ValidateTokenContext>
{
    public static OpenIddictValidationHandlerDescriptor Descriptor { get; }
        = OpenIddictValidationHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RealmValidationTokenHandler>()
            // Run BEFORE the stock IdentityModel validation so the realm-only keys +
            // issuer are in place when the JWT signature/issuer are checked.
            .SetOrder(Protection.ValidateIdentityModelToken.Descriptor.Order - 1)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    private readonly IRealmKeyStore _keyStore;

    public RealmValidationTokenHandler(IRealmKeyStore keyStore) => _keyStore = keyStore;

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TokenValidationParameters is null) return;

        var slug = TenantContext.Current;
        var keys = await _keyStore.GetVerificationKeysAsync(slug);
        context.TokenValidationParameters.IssuerSigningKeys = keys;

        // Accept the realm-specific issuer (request BaseUri) the token was issued
        // with — the stock validator would compare against the global Options.Issuer.
        if (context.BaseUri is not null)
        {
            var realmIssuer = context.BaseUri.OriginalString.TrimEnd('/');
            context.TokenValidationParameters.ValidIssuer = realmIssuer;
            context.TokenValidationParameters.ValidIssuers = new[]
            {
                realmIssuer,
                realmIssuer + "/",
            };
        }
    }
}
