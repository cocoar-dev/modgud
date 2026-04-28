using TimeToDo.Domain.Entities;

namespace TimeToDo.Domain.Repositories;

/// <summary>
/// Repository interface for Customer entity.
/// </summary>
public interface ICustomerRepository
{
    /// <summary>
    /// Gets a customer by its ID.
    /// </summary>
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all customers, optionally including archived ones.
    /// </summary>
    Task<IReadOnlyList<Customer>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default);

    /// <summary>
    /// Gets all archived customers.
    /// </summary>
    Task<IReadOnlyList<Customer>> GetArchivedAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a new customer.
    /// </summary>
    Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing customer.
    /// </summary>
    Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple customers by their IDs.
    /// </summary>
    Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Archives multiple customers.
    /// </summary>
    Task ArchiveAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Unarchives a customer.
    /// </summary>
    Task<Customer> UnarchiveAsync(Guid id, CancellationToken ct = default);
}
