using System.Collections.Concurrent;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Per-provider cache of resolved SAML SP configurations. Mirrors the role of
/// <see cref="DynamicOidcSchemeManager"/> for the OIDC side but the mechanics
/// differ: ITfoxtec.Identity.Saml2 is not an ASP.NET Core
/// <c>AuthenticationHandler</c>, so there are no
/// <see cref="Microsoft.AspNetCore.Authentication.AuthenticationScheme"/>
/// instances to register here. We instead cache a lightweight
/// <see cref="RegisteredSamlProvider"/> per enabled SAML <c>LoginProvider</c>
/// so the SAML endpoint handlers (login / acs / metadata in <c>SamlEndpoints</c>)
/// can look up the per-provider config without a Marten round-trip per request.
/// <para>
/// The cache is global (not per-tenant) because the cache key is the
/// <c>LoginProvider.Id</c> Guid which is globally unique even across realms.
/// The realm slug is stored on the cached entry so the endpoint handlers know
/// which tenant context to enter when fetching/creating the linked user.
/// </para>
/// </summary>
public class DynamicSamlSchemeManager(
    SamlFlavorRegistry flavors,
    SamlMetadataFetcher metadataFetcher,
    TimeProvider clock,
    ILogger<DynamicSamlSchemeManager> logger)
{
    private readonly ConcurrentDictionary<Guid, RegisteredSamlProvider> _cache = new();

    /// <summary>
    /// Registers or replaces the cached SAML provider config. Safe to call
    /// repeatedly — existing entry is overwritten so config changes (metadata
    /// refresh, attribute-map edits, etc.) take effect on the next SAML
    /// request without an app restart.
    /// <para>
    /// Disabled / deleted / non-SAML providers are evicted (unregister
    /// behaviour). Unknown flavor is logged and skipped — defends against
    /// stale stored Flavor strings whose <see cref="ISamlFlavor"/> impl was
    /// removed.
    /// </para>
    /// </summary>
    public async Task RegisterAsync(LoginProvider config)
    {
        if (config.IsDeleted || !config.Enabled)
        {
            await UnregisterAsync(config.Id);
            return;
        }

        if (config.Type != LoginProviderType.Saml)
        {
            // Defence-in-depth: the event-handler dispatch + the bootstrap
            // pre-filter both gate on Type already. Logging at debug level
            // because being called with the wrong type is a code bug we want
            // to notice during development without crying wolf in production.
            logger.LogDebug(
                "Auth: SAML manager called for non-SAML LoginProvider {Id} (type={Type}) — ignored",
                config.Id, config.Type);
            return;
        }

        if (!flavors.TryGet(config.Flavor, out var flavor))
        {
            logger.LogWarning(
                "Cannot register SAML LoginProvider {Id}: unknown flavor {Flavor}",
                config.Id, config.Flavor);
            return;
        }

        SamlFlavorData flavorData;
        try
        {
            flavorData = flavor.ApplyDefaults(SamlFlavorData.FromJson(config.FlavorData));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Cannot register SAML LoginProvider {Id}: FlavorData parse / defaults-apply failed",
                config.Id);
            return;
        }

        var realmSlug = TenantContext.CurrentOrNull
            ?? throw new InvalidOperationException(
                "DynamicSamlSchemeManager.RegisterAsync requires an ambient TenantContext " +
                "so the cached entry knows which realm the provider belongs to. " +
                "Callers (bootstrap, event handlers) must enter the realm's TenantContext first.");

        // Fetch / parse IdP metadata. We prefer the URL form because it
        // makes the metadata-refresh job (task #15) trivially periodic.
        // Pasted XML is the fallback for firewalled IdPs (e.g. on-prem ADFS).
        // Failure to populate IdpMetadata is non-fatal at register time —
        // login attempts will surface a clear error until metadata is
        // available.
        SamlIdpMetadata? idpMetadata = null;
        if (!string.IsNullOrWhiteSpace(flavorData.MetadataUrl))
        {
            idpMetadata = await metadataFetcher.FetchAsync(flavorData.MetadataUrl);
        }
        else if (!string.IsNullOrWhiteSpace(flavorData.MetadataXml))
        {
            idpMetadata = metadataFetcher.Parse(flavorData.MetadataXml);
        }

        var entry = new RegisteredSamlProvider(
            LoginProviderId: config.Id,
            DisplayName: config.DisplayName,
            Flavor: config.Flavor,
            RealmSlug: realmSlug,
            FlavorData: flavorData,
            IdpMetadata: idpMetadata,
            MetadataFetchedAt: idpMetadata is null ? null : clock.GetUtcNow());

        _cache[config.Id] = entry;

        if (idpMetadata is null)
        {
            logger.LogWarning(
                "Auth: Registered SAML provider {Id} ({Display}) in realm {Realm} WITHOUT IdP metadata — " +
                "login attempts will fail until metadata is reachable",
                config.Id, config.DisplayName, realmSlug);
        }
        else
        {
            logger.LogInformation(
                "Auth: Registered SAML provider {Id} ({Display} / {Flavor}) in realm {Realm} " +
                "with IdP {IdpEntity} and {CertCount} signing cert(s)",
                config.Id, config.DisplayName, config.Flavor, realmSlug,
                idpMetadata.EntityId, idpMetadata.SigningCertificatesBase64.Count);
        }
    }

    /// <summary>Evict the cache entry for the given provider. Idempotent.</summary>
    public Task UnregisterAsync(Guid loginProviderId)
    {
        if (_cache.TryRemove(loginProviderId, out var entry))
        {
            logger.LogInformation(
                "Auth: Unregistered SAML provider {Id} ({Display})",
                loginProviderId, entry.DisplayName);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lookup by provider id. Returns <c>false</c> for unknown / disabled
    /// providers — endpoint handlers translate that to a 404 to avoid
    /// disclosing provider existence to anonymous callers.
    /// </summary>
    public bool TryGet(Guid loginProviderId, out RegisteredSamlProvider? entry)
    {
        return _cache.TryGetValue(loginProviderId, out entry);
    }

    /// <summary>
    /// Snapshot of all currently-registered providers for login-page button
    /// discovery. Filters by realm so the login page only sees providers for
    /// the caller's realm.
    /// </summary>
    public IReadOnlyList<RegisteredSamlProvider> GetRegisteredForRealm(string realmSlug) =>
        _cache.Values
            .Where(e => string.Equals(e.RealmSlug, realmSlug, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Snapshot of all currently-registered providers across realms. For
    /// observability / admin diagnostics only — the public login-page should
    /// use <see cref="GetRegisteredForRealm"/>.
    /// </summary>
    public IReadOnlyList<RegisteredSamlProvider> GetAllRegistered() =>
        _cache.Values.ToArray();

    /// <summary>
    /// Re-fetches IdP metadata for an already-registered provider and
    /// updates the cache entry in-place. Used by the periodic refresh
    /// background service. Returns true on a successful fetch (whether or
    /// not the certs actually changed), false on fetch failure (kept stale).
    /// </summary>
    public async Task<bool> RefreshMetadataAsync(Guid loginProviderId, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(loginProviderId, out var existing) || existing is null)
            return false;

        SamlIdpMetadata? fresh = null;
        if (!string.IsNullOrWhiteSpace(existing.FlavorData.MetadataUrl))
        {
            fresh = await metadataFetcher.FetchAsync(existing.FlavorData.MetadataUrl, ct);
        }
        else if (!string.IsNullOrWhiteSpace(existing.FlavorData.MetadataXml))
        {
            fresh = metadataFetcher.Parse(existing.FlavorData.MetadataXml);
        }

        if (fresh is null) return false;

        var updated = existing with
        {
            IdpMetadata = fresh,
            MetadataFetchedAt = clock.GetUtcNow(),
        };
        _cache[loginProviderId] = updated;

        // Log only when the cert set actually changes — refreshes that
        // confirm the same certs are not interesting in steady state.
        if (existing.IdpMetadata is null
            || !SequenceEqual(existing.IdpMetadata.SigningCertificatesBase64, fresh.SigningCertificatesBase64))
        {
            logger.LogInformation(
                "Auth: SAML metadata refresh for provider {Id} changed signing certs " +
                "({OldCount} → {NewCount})",
                loginProviderId,
                existing.IdpMetadata?.SigningCertificatesBase64.Count ?? 0,
                fresh.SigningCertificatesBase64.Count);
        }

        return true;
    }

    private static bool SequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
