using System.Xml.Linq;
using Marten;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Cocoar.Auth.Infrastructure.Persistence.DataProtection;

/// <summary>
/// <see cref="IXmlRepository"/> bound to a single tenant's database.
/// Keys never leave that database — a master-DB compromise cannot forge
/// cookies for any tenant, and a tenant-DB compromise is contained to
/// that tenant. Aligns with the per-realm signing-key model used by
/// <c>RealmSigningKey</c>.
///
/// <para>Reads run on every framework call (cheap in our Volumina), writes
/// happen on key rollover (default every 90 days). The framework keeps an
/// in-process key-ring cache, so this isn't a hot path either way.</para>
/// </summary>
public sealed class MartenXmlRepository : IXmlRepository
{
    private readonly IDocumentStore _store;
    private readonly string _tenantId;

    public MartenXmlRepository(IDocumentStore store, string tenantId)
    {
        _store = store;
        _tenantId = tenantId;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var session = _store.QuerySession(_tenantId);
        // IXmlRepository is a sync contract; Marten 9 removed sync LINQ
        // terminals on Marten queries. Key-ring refresh happens ~once every
        // 90 days and never on a request thread, so sync-over-async here is
        // negligible. Matches AppBase v2.0.0 backport pattern.
        var docs = session.Query<DataProtectionKeyDocument>()
            .ToListAsync()
            .GetAwaiter().GetResult();
        return docs
            .Select(d => XElement.Parse(d.Xml))
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var session = _store.LightweightSession(_tenantId);
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
