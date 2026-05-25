using Modgud.Domain.OAuth.Scopes;
using Marten;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Extends <c>scopes_supported</c> in the OpenID Connect discovery document
/// with the calling realm's <see cref="OAuthScopeState"/> entries that the
/// admin has explicitly opted into public listing
/// (<c>Enabled &amp;&amp; ShowInDiscoveryDocument &amp;&amp; !IsDeleted</c>).
///
/// <para>OpenIddict's stock <see cref="Discovery.AttachScopes"/> handler
/// only emits the scopes registered statically via
/// <c>options.RegisterScopes(...)</c> (the OIDC standards: openid, email,
/// profile, offline_access, roles). Dynamic store-scopes — every
/// <see cref="OAuthScopeState"/> an admin creates through the UI — never
/// land there, regardless of how <c>ShowInDiscoveryDocument</c> is set.
/// This handler closes that gap by querying the tenant-scoped Marten
/// session at request time and appending the public scopes the admin
/// has flagged.</para>
///
/// <para><strong>Privacy is opt-in, not opt-out.</strong> Scopes created
/// through the standard admin flow default to
/// <c>ShowInDiscoveryDocument = true</c>; the implicit-scope-per-API path
/// (<c>OAuthAdminService.CreateImplicitScopeForApiAsync</c>) defaults to
/// <c>false</c> so a one-click bootstrap doesn't leak its RS name into
/// public metadata. The static-registration set (OIDC standards) is
/// emitted by the stock handler before us; we only append and never
/// remove, so legacy clients continue to discover what they always
/// could.</para>
///
/// <para>Tenant scoping: <see cref="IDocumentSession"/> is automatically
/// per-tenant (<c>TenantedSessionFactory</c> reads
/// <c>HttpContext.Items["TenantId"]</c> set by the realm middleware), so a
/// realm-A discovery probe never sees realm-B's scopes. The query is
/// cheap (single indexed scan over a typically-tiny table) and runs once
/// per discovery hit — same cadence as the stock handler.</para>
/// </summary>
public sealed class RealmScopesSupportedHandler
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseScopedHandler<RealmScopesSupportedHandler>()
            // Run AFTER the stock AttachScopes handler so we observe its
            // already-populated list and append; ordering it before would
            // either get our additions overwritten or duplicated depending
            // on the stock handler's behaviour.
            .SetOrder(Discovery.AttachScopes.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;

    public RealmScopesSupportedHandler(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var publicScopes = await _session.Query<OAuthScopeState>()
            .Where(s => !s.IsDeleted && s.Enabled && s.ShowInDiscoveryDocument)
            .Select(s => s.Name)
            .ToListAsync();

        // `context.Scopes` is a HashSet<string>; UnionWith mirrors what the
        // stock AttachScopes handler does for its own input list, so the
        // resulting `scopes_supported` is the union of static OIDC scopes
        // plus this realm's opted-in dynamic scopes — deduplicated by the
        // set semantics, no manual Contains-check needed.
        context.Scopes.UnionWith(publicScopes);
    }
}
