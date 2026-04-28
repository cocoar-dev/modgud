using Marten;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using LibEngine = TimeToDo.Authorization.Access.IAccessPolicyEngine;

namespace TimeToDo.Infrastructure.AccessPolicy;

/// <summary>
/// User-specific environment injected into access policy scripts as <c>env</c>.
/// Provides lazily-computed helpers so scripts can await cross-resource queries
/// without triggering DB work when the helper is never called.
/// Each method caches its result for the lifetime of the script execution —
/// repeated calls pay only one DB round-trip.
/// New helpers can be added here as the access model grows.
/// </summary>
public sealed class AccessPolicyEnvironment
{
    private readonly Lazy<Task<Guid[]>> _allowedCustomerIds;

    internal AccessPolicyEnvironment(Guid userId, LibEngine inner, IQuerySession session, CancellationToken ct)
    {
        _allowedCustomerIds = new Lazy<Task<Guid[]>>(
            () => ResolveAllowedCustomerIdsAsync(userId, inner, session, ct));
    }

    /// <summary>
    /// Returns the IDs of all customers the user has read access to.
    /// When the user has unrestricted customer access (null filter), returns ALL non-deleted customer IDs.
    /// </summary>
    public Task<Guid[]> AllowedCustomerIds() => _allowedCustomerIds.Value;

    private static async Task<Guid[]> ResolveAllowedCustomerIdsAsync(
        Guid userId, LibEngine inner, IQuerySession session, CancellationToken ct)
    {
        var filter = await inner.BuildFilterAsync<CustomerView>(userId, "customer", ct: ct);

        var query = session.Query<CustomerView>().Where(c => !c.IsDeleted);
        if (filter is not null)
            query = query.Where(filter);

        var ids = await query.Select(c => c.Id).ToListAsync(ct);
        return ids.ToArray();
    }
}
