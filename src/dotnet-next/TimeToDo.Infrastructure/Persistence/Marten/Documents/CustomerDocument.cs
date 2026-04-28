namespace TimeToDo.Infrastructure.Persistence.Marten.Documents;

/// <summary>
/// Customer document - stored as separate document in Marten
/// </summary>
public class CustomerDocument
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    // Flags as explicit properties (easier with documents - no migration needed!)
    public bool IsImportant { get; set; }

    public bool IsArchived { get; set; }
}
