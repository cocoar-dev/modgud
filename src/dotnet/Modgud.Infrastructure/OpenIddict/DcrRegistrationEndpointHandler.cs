using Marten;
using OpenIddict.Server;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Adds the <c>registration_endpoint</c> entry to
/// <c>/.well-known/openid-configuration</c> — but only for realms where
/// the admin has flipped on Dynamic Client Registration in
/// <c>RealmSettings.Dcr</c>. A disabled realm's discovery document is
/// indistinguishable from one on a server that doesn't speak DCR at
/// all, so drive-by enumerators can't tell whether the feature exists
/// per realm.
///
/// <para>OpenIddict 7 has no built-in <c>SetRegistrationEndpointUris()</c>
/// option, so this handler is the canonical way to advertise the
/// endpoint. <c>HandleConfigurationRequestContext.Metadata</c> is a
/// free-form dict that gets serialised into the discovery JSON
/// response after the typed properties are emitted — perfect for the
/// optional/extension fields RFC 7591 §4 defines.</para>
///
/// <para>Mirrors the privacy / ordering / tenancy stance of
/// <see cref="RealmScopesSupportedHandler"/> (which advertises
/// <c>scopes_supported</c>): per-realm settings drive the inclusion
/// decision; <see cref="IDocumentSession"/> is tenant-scoped via
/// <c>TenantedSessionFactory</c>, so a realm-A discovery probe never
/// sees realm-B's DCR config. We load the RealmSettings singleton doc
/// directly to keep this handler in the Infrastructure layer (no
/// Authentication-layer service dep).</para>
/// </summary>
public sealed class DcrRegistrationEndpointHandler
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseScopedHandler<DcrRegistrationEndpointHandler>()
            // Run after the stock endpoint-attach handlers so we're
            // operating on a fully-populated context — same convention
            // as RealmScopesSupportedHandler. AttachEndpoints is the
            // last handler that touches endpoint URLs in the default
            // discovery pipeline.
            .SetOrder(Discovery.AttachEndpoints.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;

    public DcrRegistrationEndpointHandler(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await _session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId);
        if (settings?.Dcr is null || !settings.Dcr.Enabled) return;

        // context.BaseUri is the realm-aware issuer-root populated by
        // OpenIddict's ResolveRequestUri handler (and adopted by our
        // RealmIssuerHandler). Building the endpoint URL from it
        // — rather than hardcoding a host — keeps the value correct
        // across realm hostnames and reverse-proxy paths.
        if (context.BaseUri is null) return;

        var registrationUri = new Uri(context.BaseUri, "connect/register").ToString();
        context.Metadata["registration_endpoint"] = registrationUri;
    }
}
