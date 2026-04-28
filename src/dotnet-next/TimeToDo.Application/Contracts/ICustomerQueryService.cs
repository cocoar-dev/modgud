using TimeToDo.Application.DTOs.Customer;

namespace TimeToDo.Application.Contracts;

/// <summary>
/// Query service for retrieving Customer DTOs.
/// </summary>
public interface ICustomerQueryService
{
    Task<IReadOnlyList<CustomerDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CustomerDto>> GetArchivedAsync(CancellationToken ct = default);
    Task<CustomerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
