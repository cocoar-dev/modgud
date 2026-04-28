using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Repositories;

/// <summary>
/// Marten implementation of ICustomerRepository.
/// </summary>
public class MartenCustomerRepository : ICustomerRepository
{
    private readonly IDocumentSession _session;
    private readonly DataEventDispatcher _eventDispatcher;
    private const string EventSubject = "Customer";

    public MartenCustomerRepository(IDocumentSession session, DataEventDispatcher eventDispatcher)
    {
        _session = session;
        _eventDispatcher = eventDispatcher;
    }

    public async Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CustomerDocument>(id, ct);
        return document?.ToDomainEntity();
    }

    public async Task<IReadOnlyList<Customer>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var query = _session.Query<CustomerDocument>();

        var documents = includeArchived
            ? await query.ToListAsync(ct)
            : await query.Where(c => !c.IsArchived).ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<IReadOnlyList<Customer>> GetArchivedAsync(CancellationToken ct = default)
    {
        var documents = await _session.Query<CustomerDocument>()
            .Where(c => c.IsArchived)
            .ToListAsync(ct);

        return documents.Select(d => d.ToDomainEntity()).ToList();
    }

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default)
    {
        var document = customer.ToDocument();
        _session.Store(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        return customer;
    }

    public async Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CustomerDocument>(customer.Id, ct);
        if (document == null)
            throw new InvalidOperationException($"Customer with ID {customer.Id} not found");

        document.UpdateFromEntity(customer);
        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchUpdatedEvent(EventSubject, document);

        return customer;
    }

    public async Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        _session.DeleteWhere<CustomerDocument>(c => c.Id.In(idList));
        await _session.SaveChangesAsync(ct);

        var shortIds = idList.Select(id => new ShortGuid(id).ToString());
        _eventDispatcher.DispatchEvent(DataEvent.Deleted(EventSubject, shortIds));
    }

    public async Task ArchiveAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        var documents = await _session.Query<CustomerDocument>()
            .Where(c => c.Id.In(idList))
            .ToListAsync(ct);

        foreach (var document in documents)
        {
            document.IsArchived = true;
            _session.Update(document);
        }

        await _session.SaveChangesAsync(ct);

        var shortIds = idList.Select(id => new ShortGuid(id).ToString());
        _eventDispatcher.DispatchEvent(DataEvent.Deleted(EventSubject, shortIds));
    }

    public async Task<Customer> UnarchiveAsync(Guid id, CancellationToken ct = default)
    {
        var document = await _session.LoadAsync<CustomerDocument>(id, ct);
        if (document == null)
            throw new InvalidOperationException($"Customer with ID {id} not found");

        document.IsArchived = false;
        _session.Update(document);
        await _session.SaveChangesAsync(ct);

        _eventDispatcher.DispatchCreatedEvent(EventSubject, document);

        return document.ToDomainEntity();
    }
}
