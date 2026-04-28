using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.Customer;

public class CustomerListDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }

    public bool Important { get; set; }
    public bool IsArchived { get; set; }
    public EntityStatus Status { get; set; } = EntityStatus.Active;
}
