using BuildingBlocks.Helper;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;

namespace TimeToDo.Infrastructure.Persistence.Marten.Mappers;

public static class CustomerViewMapper
{
    public static CustomerDto ToDto(this CustomerView view)
    {
        return new CustomerDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Name = view.Name ?? "",
            Important = view.IsImportant,
            IsArchived = view.IsArchived
        };
    }

    public static CustomerListDto ToListDto(this CustomerView view)
    {
        return new CustomerListDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Name = view.Name ?? "",
            Important = view.IsImportant,
            IsArchived = view.IsArchived
        };
    }
}
