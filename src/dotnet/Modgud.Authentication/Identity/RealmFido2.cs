using Fido2NetLib;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Builds the WebAuthn relying party (<see cref="IFido2"/>) for the CURRENT
/// realm. The RP ID is the realm's <see cref="Realm.PrimaryDomain"/>, so a
/// passkey is bound to the realm it was registered on — never to a single
/// global host. The same realm (hence the same RP) is resolved on both the
/// registration and the assertion request, which is mandatory: a credential
/// created against one RP ID can only be verified against that same RP ID.
///
/// <para>Wired via the scoped <see cref="RealmScopedFido2Factory"/> in DI
/// (replacing the library's global <c>AddFido2</c> registration). Each
/// WebAuthn endpoint awaits the factory once per request to build a matching
/// <see cref="Fido2"/>.</para>
/// </summary>
public static class RealmFido2
{
    /// <summary>
    /// Builds a <see cref="Fido2Configuration"/> for the given realm.
    /// Production origin = <c>https://{PrimaryDomain}</c>; Development adds the
    /// SPA dev-server origin (<c>http://{PrimaryDomain}:4300</c>) since that is
    /// where the WebAuthn ceremony runs in dev. <c>localhost</c> /
    /// <c>*.localhost</c> are browser secure-contexts, so dev passkeys work.
    /// </summary>
    public static Fido2Configuration BuildConfiguration(Realm realm, IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(env);

        var host = realm.PrimaryDomain;
        // A passkey's RP ID IS the primary domain — an empty one would mint
        // unverifiable credentials. Every realm must have a primary domain
        // (enforced at create/update/adopt + backfilled at boot); fail loudly
        // if that invariant was somehow violated rather than build a bad RP.
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(
                $"Realm '{realm.Slug}' has no PrimaryDomain — cannot build a WebAuthn relying party.");
        var origins = new HashSet<string>(StringComparer.Ordinal);
        if (env.IsDevelopment())
        {
            // Dev: the SPA runs on the Vite dev-server port; the WebAuthn
            // origin the browser reports is the page origin.
            origins.Add($"http://{host}:{RealmPublicUrl.DevSpaPort}");
            // Tolerate a plain-host origin too (e.g. when the SPA is served
            // straight off the API in a dev container).
            origins.Add($"https://{host}");
            origins.Add($"http://{host}");
        }
        else
        {
            origins.Add($"https://{host}");
        }

        return new Fido2Configuration
        {
            // RP ID — the effective domain a passkey is bound to.
            ServerDomain = host,
            ServerName = string.IsNullOrWhiteSpace(realm.DisplayName) ? "Modgud" : realm.DisplayName,
            Origins = origins,
        };
    }
}

/// <summary>
/// Scoped DI factory: resolves the current realm from the ambient request and
/// builds an <see cref="IFido2"/> whose RP is that realm's primary domain.
/// Throws when no realm can be resolved — a passkey ceremony without a
/// resolvable realm has no valid RP and must fail loudly, never fall back to a
/// wrong host (which would silently produce unverifiable credentials).
/// </summary>
public sealed class RealmScopedFido2Factory(
    IHttpContextAccessor httpContextAccessor,
    IRealmProvisioningService realmSvc,
    IWebHostEnvironment env,
    IMetadataService? metadataService = null)
{
    public async Task<IFido2> CreateAsync(CancellationToken ct = default)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "RealmScopedFido2Factory requires an active HttpContext — WebAuthn ceremonies are request-bound.");

        var realm = await http.ResolveCurrentRealmAsync(realmSvc, ct)
            ?? throw new InvalidOperationException(
                "Could not resolve the current realm for the WebAuthn ceremony — no relying party can be built.");

        var config = RealmFido2.BuildConfiguration(realm, env);
        // metadataService is optional — the previous global setup used the
        // library's NullMetadataService (no attestation-metadata validation),
        // and passing null here gives the identical behaviour.
        return new Fido2(config, metadataService);
    }
}
