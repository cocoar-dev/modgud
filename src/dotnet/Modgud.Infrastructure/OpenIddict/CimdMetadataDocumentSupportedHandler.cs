using Marten;
using OpenIddict.Server;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Advertises <c>client_id_metadata_document_supported: true</c> in
/// <c>/.well-known/openid-configuration</c> — but only for realms where the
/// admin has flipped on CIMD in <c>RealmSettings.Cimd</c>. A
/// disabled realm's discovery document is indistinguishable from one on a
/// server that doesn't speak CIMD, so drive-by enumerators can't tell whether
/// the feature exists per realm.
///
/// <para>Mirrors <see cref="DcrRegistrationEndpointHandler"/> exactly:
/// per-realm settings drive the inclusion decision;
/// <see cref="IDocumentSession"/> is tenant-scoped via
/// <c>TenantedSessionFactory</c> so a realm-A discovery probe never sees
/// realm-B's CIMD config; <c>HandleConfigurationRequestContext.Metadata</c>
/// is the free-form extension-field dict serialised into the discovery JSON
/// after the typed properties.</para>
/// </summary>
public sealed class CimdMetadataDocumentSupportedHandler
    : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    private const string MetadataKey = "client_id_metadata_document_supported";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleConfigurationRequestContext>()
            .UseScopedHandler<CimdMetadataDocumentSupportedHandler>()
            // Run after the stock endpoint-attach handlers so we operate on a
            // fully-populated context — same convention as the DCR handler.
            .SetOrder(Discovery.AttachEndpoints.Descriptor.Order + 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;

    public CimdMetadataDocumentSupportedHandler(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await _session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId);
        if (settings?.Cimd is null || !settings.Cimd.Enabled) return;

        context.Metadata[MetadataKey] = true;
    }
}
