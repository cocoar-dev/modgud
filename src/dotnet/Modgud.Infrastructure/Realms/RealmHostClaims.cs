using ErrorOr;
using Marten;
using Modgud.Domain.Realms;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// A host routes to exactly one active realm (audit M10, F.routing-F-R1, RV-0002): it may be
/// neither a domain nor an Application subdomain of another active realm. Application subdomains
/// count because they win in the routing lookup (<c>RealmCacheLookup</c>): a realm that listed
/// another realm's App host as its domain never received that host's traffic. Compared
/// case-insensitively; inactive realms are not routed and do not count. The own realm's entries
/// are no conflict.
/// </summary>
public static class RealmHostClaims
{
    public static async Task<Error?> FindConflictAsync(
        IQuerySession session, IEnumerable<string> hosts, Guid? selfId, CancellationToken ct)
    {
        var wanted = hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).ToHashSet();
        if (wanted.Count == 0) return null;

        var activeRealms = await session.Query<Realm>().Where(r => r.IsActive).ToListAsync(ct);
        foreach (var other in activeRealms)
        {
            if (selfId is { } id && other.Id == id) continue;
            var clash = other.Domains.FirstOrDefault(d => wanted.Contains(d.ToLowerInvariant()))
                        ?? other.ApplicationDomains.Keys.FirstOrDefault(d => wanted.Contains(d.ToLowerInvariant()));
            if (clash is not null)
                return Error.Conflict("Realm.DomainTaken",
                    $"The host '{clash}' is already claimed by the active realm '{other.Slug}'. Each host must map to exactly one active realm.");
        }
        return null;
    }
}
