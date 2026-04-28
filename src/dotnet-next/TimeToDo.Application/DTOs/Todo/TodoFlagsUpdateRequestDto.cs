namespace TimeToDo.Application.DTOs.Todo;

public class TodoFlagsUpdateRequestDto
{
    public List<string> Ids { get; set; } = new();

    public List<string>? AddFlags { get; set; }

    public List<string>? RemoveFlags { get; set; }
}
