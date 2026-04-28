namespace TimeToDo.Application.DTOs;

public class RefPropertyDto
{
    public required string Id { get; set; }

    public string? Label { get; set; }

    /// <summary>
    /// For principal references (Responsibles, CreatedBy, UpdatedBy, etc.): indicates
    /// the principal kind ("Person" or "Group"). Null for non-principal refs (e.g. Customer).
    /// The UI uses this to show appropriate icons and to expand groups when needed.
    /// </summary>
    public string? PrincipalType { get; set; }
}
