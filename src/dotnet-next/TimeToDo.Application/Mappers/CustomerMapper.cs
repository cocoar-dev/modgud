using BuildingBlocks.Helper;
using Riok.Mapperly.Abstractions;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Domain.Entities;

namespace TimeToDo.Application.Mappers;

[Mapper]
public static partial class CustomerMapper
{
    public static CustomerDto ToDto(this Customer entity)
    {
        return new CustomerDto
        {
            Id = new ShortGuid(entity.Id).ToString(),
            Name = entity.Name,
            Important = entity.IsImportant,
            IsArchived = entity.IsArchived
        };
    }
}
