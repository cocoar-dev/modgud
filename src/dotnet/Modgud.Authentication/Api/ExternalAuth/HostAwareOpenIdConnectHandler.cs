using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Drop-in replacement for the framework <see cref="OpenIdConnectHandler"/> that
/// adds a per-tenant tiebreaker to remote-callback handling.
/// <para>
/// The external-OIDC callback path is the admin-chosen provider <c>slug</c>
/// (<c>/signin-oidc/{slug}</c>), which is only unique per realm. The base
/// handler's <see cref="ShouldHandleRequestAsync"/> matches the callback purely
/// on path and is host-blind, so when two realms register the same slug their
/// schemes both claim the same callback path. We disambiguate by comparing the
/// realm this scheme belongs to (via <see cref="OidcSchemeRealmRegistry"/>)
/// against the realm the current request resolved to — set on
/// <c>HttpContext.Items</c> by <c>RealmMiddleware</c>, which runs before
/// <c>UseAuthentication</c>. Only the matching realm's scheme handles the
/// callback; the others decline so the framework keeps looking.
/// </para>
/// </summary>
public sealed class HostAwareOpenIdConnectHandler : OpenIdConnectHandler
{
    private readonly OidcSchemeRealmRegistry _realmRegistry;

    public HostAwareOpenIdConnectHandler(
        IOptionsMonitor<OpenIdConnectOptions> options,
        ILoggerFactory logger,
        HtmlEncoder htmlEncoder,
        UrlEncoder encoder,
        OidcSchemeRealmRegistry realmRegistry)
        : base(options, logger, htmlEncoder, encoder)
    {
        _realmRegistry = realmRegistry;
    }

    public override async Task<bool> ShouldHandleRequestAsync()
    {
        // base matches our CallbackPath / SignedOutCallbackPath / RemoteSignOutPath.
        if (!await base.ShouldHandleRequestAsync())
            return false;

        // Untracked scheme (shouldn't happen for dynamically-registered schemes)
        // — fall back to the base path-only behaviour rather than swallow it.
        if (!_realmRegistry.TryGetRealm(Scheme.Name, out var schemeRealm))
            return true;

        var currentRealm = Context.Items.TryGetValue(TenantConstants.HttpContextTenantIdKey, out var v)
            ? v as string
            : null;

        // Only the scheme whose realm matches the current request handles it.
        return string.Equals(currentRealm, schemeRealm, StringComparison.Ordinal);
    }
}
