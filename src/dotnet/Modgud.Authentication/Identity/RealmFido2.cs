using System.Text.Json;
using Fido2NetLib;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Thrown when a WebAuthn relying party cannot be built for the current realm —
/// e.g. the realm has no <see cref="Realm.PrimaryDomain"/> (the RP ID). It is a
/// realm-misconfiguration condition, not a server bug, so the passkey endpoints
/// map it to a clear, actionable response instead of a bare 500.
/// </summary>
public sealed class RelyingPartyUnavailableException(string message) : Exception(message);

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
    ///
    /// <para><paramref name="additionalOrigins"/> — origins the authenticator
    /// actually signed (read from the ceremony's clientDataJSON). Each is accepted
    /// ONLY if it is the RP-ID host or a subdomain of it (see
    /// <see cref="IsOriginUnderRpId"/>). This is what makes a per-client RP-ID that
    /// is a registrable SUFFIX of the app origin work: RP-ID <c>amzettel.at</c> for a
    /// page on <c>app.amzettel.at</c> is spec-valid (the browser only ever presents
    /// an origin whose effective domain has the RP-ID as a suffix), but deriving the
    /// accepted origin as <c>https://{rpId}</c> wrongly rejected it. The RP-ID hash
    /// and signature checks remain the primary boundary; this only widens the origin
    /// allow-list to the set the WebAuthn spec already scopes to this RP-ID.</para>
    /// </summary>
    public static Fido2Configuration BuildConfiguration(
        Realm realm,
        IWebHostEnvironment env,
        string? rpIdOverride = null,
        IEnumerable<string>? additionalOrigins = null)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(env);

        // ADR-0009 implementation seam (per-client WebAuthn RP-ID). When the
        // override is null — the only Phase-2 caller — behaviour is bit-identical
        // to today (RP-ID = realm.PrimaryDomain). When a future caller (Phase 3,
        // per-client RP-ID) supplies an admin-set value, it becomes BOTH the
        // ServerDomain (RP-ID) and the host the https origin is derived from, so
        // the RP-ID and the accepted origin can never disagree.
        var host = string.IsNullOrWhiteSpace(rpIdOverride) ? realm.PrimaryDomain : rpIdOverride;
        // A passkey's RP ID IS the primary domain — an empty one would mint
        // unverifiable credentials. Every realm must have a primary domain
        // (enforced at create/update/adopt + backfilled at boot); fail loudly
        // if that invariant was somehow violated rather than build a bad RP. A
        // dedicated exception lets the passkey endpoints surface this as a clear
        // "passkeys unavailable for this realm" response instead of a bare 500.
        if (string.IsNullOrWhiteSpace(host))
            throw new RelyingPartyUnavailableException(
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

        // Widen the accepted origins to the actual signed origin(s), but only those
        // genuinely under this RP-ID — never a foreign host.
        if (additionalOrigins is not null)
        {
            foreach (var origin in additionalOrigins)
            {
                if (IsOriginUnderRpId(origin, host, env.IsDevelopment()))
                    origins.Add(origin);
            }
        }

        return new Fido2Configuration
        {
            // RP ID — the effective domain a passkey is bound to.
            ServerDomain = host,
            ServerName = string.IsNullOrWhiteSpace(realm.DisplayName) ? "Modgud" : realm.DisplayName,
            Origins = origins,
        };
    }

    /// <summary>
    /// True when <paramref name="origin"/> is an absolute <c>https</c> URL (also
    /// <c>http</c> when <paramref name="allowInsecure"/>, i.e. dev) whose host equals
    /// <paramref name="rpId"/> or is a subdomain of it — exactly the set of origins
    /// WebAuthn already scopes to this RP-ID. The dotted-suffix test (<c>host</c> ends
    /// with <c>"." + rpId</c>) deliberately rejects look-alikes like
    /// <c>amzettel.at.evil.com</c> and <c>evilamzettel.at</c>.
    /// </summary>
    public static bool IsOriginUnderRpId(string? origin, string? rpId, bool allowInsecure)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(rpId)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
            return false;
        return string.Equals(uri.Host, rpId, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a WebAuthn origin is the exact origin of the current HTTP request.
    /// Hosted web ceremonies are same-origin, so accepting the browser-presented
    /// origin must not silently widen the verifier to a different RP subdomain or
    /// port. Forwarded-header middleware has already normalized the request scheme
    /// and host before the endpoint calls this helper.
    /// </summary>
    public static bool IsOriginForRequest(string? origin, string? requestScheme, HostString requestHost)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(requestScheme) || !requestHost.HasValue)
            return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var expectedPort = requestHost.Port
            ?? (string.Equals(requestScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        return string.Equals(uri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == expectedPort;
    }

    /// <summary>
    /// Extracts the WebAuthn <c>origin</c> from a clientDataJSON byte payload (the
    /// value Fido2NetLib already base64url-decoded onto the raw response). Returns
    /// <c>null</c> on any malformed input — the caller then simply passes no extra
    /// origin and the verify fails closed as before.
    /// </summary>
    public static string? TryGetClientDataOrigin(byte[]? clientDataJson)
    {
        if (clientDataJson is null || clientDataJson.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            return doc.RootElement.TryGetProperty("origin", out var o) ? o.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
    public async Task<IFido2> CreateAsync(
        CancellationToken ct = default,
        string? rpIdOverride = null,
        IEnumerable<string>? additionalOrigins = null)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "RealmScopedFido2Factory requires an active HttpContext — WebAuthn ceremonies are request-bound.");

        var realm = await http.ResolveCurrentRealmAsync(realmSvc, ct)
            ?? throw new InvalidOperationException(
                "Could not resolve the current realm for the WebAuthn ceremony — no relying party can be built.");

        // ADR-0009 seam: rpIdOverride is null for every Phase-2 caller (RP-ID stays
        // realm.PrimaryDomain). Threaded through so the Phase-3 per-client RP-ID
        // only has to supply the value here — no new RP-ID code path.
        // additionalOrigins carries the actual signed origin at verify time so a
        // per-client RP-ID that is a suffix of the app origin is accepted.
        var config = RealmFido2.BuildConfiguration(realm, env, rpIdOverride, additionalOrigins);
        // metadataService is optional — the previous global setup used the
        // library's NullMetadataService (no attestation-metadata validation),
        // and passing null here gives the identical behaviour.
        return new Fido2(config, metadataService);
    }
}
