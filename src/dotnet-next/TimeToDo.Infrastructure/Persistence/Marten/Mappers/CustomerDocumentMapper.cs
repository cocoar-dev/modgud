using BuildingBlocks.Helper;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Domain.Entities;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

/// <summary>
/// Maps between Customer domain entity and CustomerDocument persistence model.
/// </summary>
public static class CustomerDocumentMapper
{
    public static Customer ToDomainEntity(this CustomerDocument document)
    {
        return Customer.Reconstitute(
            id: document.Id,
            name: document.Name,
            isImportant: document.IsImportant,
            isArchived: document.IsArchived
        );
    }

    public static CustomerDocument ToDocument(this Customer entity)
    {
        return new CustomerDocument
        {
            Id = entity.Id,
            Name = entity.Name,
            IsImportant = entity.IsImportant,
            IsArchived = entity.IsArchived
        };
    }

    public static void UpdateFromEntity(this CustomerDocument document, Customer entity)
    {
        document.Name = entity.Name;
        document.IsImportant = entity.IsImportant;
        document.IsArchived = entity.IsArchived;
    }

    // Document → DTO mappings for API handlers
    public static CustomerDto ToDto(this CustomerDocument document)
    {
        return new CustomerDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Name = document.Name,
            Important = document.IsImportant,
            IsArchived = document.IsArchived
        };
    }

    public static CustomerListDto ToListDto(this CustomerDocument document)
    {
        return new CustomerListDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Name = document.Name,
            Important = document.IsImportant,
            IsArchived = document.IsArchived
        };
    }
}
