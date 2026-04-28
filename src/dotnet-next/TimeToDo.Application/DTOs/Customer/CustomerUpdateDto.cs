using TimeToDo.Domain.Common;

namespace TimeToDo.Application.DTOs.Customer;

public class CustomerUpdateDto
{
    public Optional<string> Name { get; set; }
    public Optional<bool> Important { get; set; }
    public Optional<bool> IsArchived { get; set; }
}
