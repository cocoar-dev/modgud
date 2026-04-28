namespace TimeToDo.Infrastructure.Persistence.Marten.Documents;

/// <summary>
/// User document - stored as separate document in Marten
/// </summary>
public class UserDocument
{
    public Guid Id { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
}
