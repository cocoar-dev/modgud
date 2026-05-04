using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Replaces the JWKS document with the calling realm's keys only. Runs in
/// place of the stock <see cref="Discovery.AttachSigningKeys"/> handler:
/// we register first, populate <see cref="HandleJsonWebKeySetRequestContext.Keys"/>
/// from <see cref="IRealmKeyStore"/>, and let the default handler observe
/// the already-populated list and add nothing.
///
/// <para>
/// Why this matters: a resource server fetching realm A's discovery doc
/// pulls its <c>jwks_uri</c> and gets back ONLY realm A's public keys.
/// A token signed by realm B's key (different KID, different RSA modulus)
/// fails signature validation immediately — there's no key in the trusted
/// set that matches. This is the cryptographic gate that makes per-realm
/// isolation actually mean something.
/// </para>
/// </summary>
public sealed class RealmJwksHandler : IOpenIddictServerHandler<HandleJsonWebKeySetRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleJsonWebKeySetRequestContext>()
            .UseScopedHandler<RealmJwksHandler>()
            // Run AFTER the stock AttachSigningKeys handler so we can REPLACE
            // its output. The default handler appends every key from the
            // server's global pool (e.g. AddDevelopmentSigningCertificate's
            // dev cert), which would otherwise leak into every realm's JWKS
            // document — letting an attacker who can sign with the dev key
            // produce tokens that any realm's resource servers accept.
            .SetOrder(Discovery.AttachSigningKeys.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IRealmKeyStore _keyStore;

    public RealmJwksHandler(IRealmKeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    public async ValueTask HandleAsync(HandleJsonWebKeySetRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var slug = TenantContext.Current;
        var keys = await _keyStore.GetVerificationKeysAsync(slug);

        // Wipe whatever the stock handler appended so the JWKS doc carries
        // ONLY this realm's keys. Without this, the global dev/production
        // signing cert (used by us to sign authorization codes that never
        // leave the IdP) would also appear here — and resource servers
        // would happily accept tokens signed by it, defeating per-realm
        // isolation.
        context.Keys.Clear();

        // Build JWK entries for each public key. We include the active key
        // and any retired keys still inside the rotation overlap window.
        // Retired keys carry use="sig" too so existing-token validation
        // continues until the overlap expires.
        foreach (var key in keys)
        {
            if (key is not RsaSecurityKey rsa) continue;
            var parameters = rsa.Rsa?.ExportParameters(false) ?? rsa.Parameters;

            var jwk = new JsonWebKey
            {
                Kty = "RSA",
                Use = "sig",
                Alg = SecurityAlgorithms.RsaSha256,
                Kid = rsa.KeyId,
                N = Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Modulus!)),
                E = Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Exponent!)),
            };
            context.Keys.Add(jwk);
        }
    }

    private static byte[] TrimLeadingZeros(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length - 1 && bytes[i] == 0) i++;
        return i == 0 ? bytes : bytes[i..];
    }
}
