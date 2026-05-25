using Modgud.Authorization.Principals;
using Marten;

namespace Modgud.Authorization.Services;

public class PrincipalLookupService(IQuerySession session) : IPrincipalLookupService
{
    public async Task<Principal?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var principal = await session.LoadAsync<Principal>(id, ct);
        return principal is { IsDeleted: false } ? principal : null;
    }

    public async Task<IReadOnlyList<Principal>> GetManyByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idArray = ids.Distinct().ToArray();
        if (idArray.Length == 0) return [];

        var principals = await session.Query<Principal>()
            .Where(p => p.Id.IsOneOf(idArray) && !p.IsDeleted)
            .ToListAsync(ct);

        // Preserve input order; drop missing
        var byId = principals.ToDictionary(p => p.Id);
        return idArray.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public async Task<IReadOnlyList<TPrincipal>> QueryByTypeAsync<TPrincipal>(CancellationToken ct = default)
        where TPrincipal : Principal
    {
        return await session.Query<TPrincipal>()
            .Where(p => !p.IsDeleted && p.IsActive)
            .ToListAsync(ct);
    }
}
