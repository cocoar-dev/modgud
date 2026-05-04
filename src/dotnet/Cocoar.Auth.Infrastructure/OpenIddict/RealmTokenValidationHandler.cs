using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Restricts JWT signature validation at the IdP boundary to the keys of
/// the active realm only. Without this, a token signed for realm A would
/// also validate against realm B's UserInfo endpoint as long as realm A's
/// key happened to be loaded somewhere in the global pool — defeating the
/// point of having per-realm keys in the first place.
///
/// <para>
/// Runs only for JWT-format tokens (access tokens for clients with
/// <c>AccessTokenType.Jwt</c>). Reference tokens go through a separate
/// store-lookup path that's already realm-isolated by virtue of the
/// per-tenant Marten store.
/// </para>
///
/// <para>
/// The companion <see cref="RealmSigningKeyHandler"/> ensures issued tokens
/// carry the realm's key; this handler is the inverse — accept ONLY the
/// realm's key on incoming token validation. Together they are the
/// crypto isolation gate.
/// </para>
/// </summary>
public sealed class RealmTokenValidationHandler : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RealmTokenValidationHandler>()
            // Run AFTER the stock handlers that build the default
            // TokenValidationParameters from Options.SigningCredentials, so
            // we can replace the IssuerSigningKeys list with realm-only keys.
            .SetOrder(Protection.ValidateIdentityModelToken.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IRealmKeyStore _keyStore;

    public RealmTokenValidationHandler(IRealmKeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Skip non-JWT formats (reference tokens, etc.) — the JWT signature
        // pipeline doesn't run for them.
        if (context.TokenValidationParameters is null) return;
        if (context.IsReferenceToken) return;

        var slug = TenantContext.Current;
        var keys = await _keyStore.GetVerificationKeysAsync(slug);

        // Replace the trusted-keys list. ValidIssuer / ValidAudience are
        // left to the stock handlers — those are realm-agnostic checks
        // (the issuer URI per-realm story lands in C3c).
        context.TokenValidationParameters.IssuerSigningKeys = keys;
    }
}
