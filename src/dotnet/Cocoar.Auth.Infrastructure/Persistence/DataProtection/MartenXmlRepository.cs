using System.Xml.Linq;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Cocoar.Auth.Infrastructure.Persistence.DataProtection;

/// <summary>
/// <see cref="IXmlRepository"/> backed by Marten in the system tenant.
/// Lets every API instance share one DataProtection-key pool — Cookies +
/// Antiforgery + any other DataProtection-protected payload survives
/// Container-Restarts and (when scaled out) cross-instance round-trip.
///
/// <para>Reads run on every framework call (cheap in our Volumina), writes
/// happen on key rollover (default every 90 days). The framework keeps an
/// in-process key-ring cache, so this isn't a hot path either way.</para>
/// </summary>
public sealed class MartenXmlRepository : IXmlRepository
{
    private readonly IDocumentStore _store;

    public MartenXmlRepository(IDocumentStore store)
    {
        _store = store;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var session = _store.QuerySession(TenantConstants.SystemTenantId);
        var docs = session.Query<DataProtectionKeyDocument>().ToList();
        return docs
            .Select(d => XElement.Parse(d.Xml))
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var session = _store.LightweightSession(TenantConstants.SystemTenantId);
        session.Store(new DataProtectionKeyDocument
        {
            // friendlyName is framework-supplied and stable (UUID-shaped),
            // safe as the document key.
            Id = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        // IXmlRepository is a sync contract; Marten 8 dropped the
        // synchronous SaveChanges overload. Key rollovers happen ~once
        // every 90 days per the DataProtection default — the sync-over-
        // async cost here is negligible and the call site (DataProtection
        // initialization) is not on a request-thread.
        session.SaveChangesAsync().GetAwaiter().GetResult();
    }
}
