using TimeToDo.Authorization.Principals;

namespace TimeToDo.Authorization.Services;

/// <summary>
/// Cross-type principal lookup. Every method returns a <see cref="Principal"/>
/// base reference — callers pattern-match against the concrete subclass
/// (Person, Group, ServiceAccount, …) to access type-specific fields.
/// </summary>
public interface IPrincipalLookupService
{
    /// <summary>Returns the principal or null if not found / deleted.</summary>
    Task<Principal?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns principals by id, preserving input order. Missing / deleted ids are silently dropped.</summary>
    Task<IReadOnlyList<Principal>> GetManyByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Returns all active (non-deleted) principals of the given concrete type
    /// — relies on Marten sub-class mapping so the filter is SQL-level, not
    /// an in-memory scan.
    /// </summary>
    Task<IReadOnlyList<TPrincipal>> QueryByTypeAsync<TPrincipal>(CancellationToken ct = default)
        where TPrincipal : Principal;
}
