namespace TimeToDo.Authentication;

public interface IDemoSeedService
{
    Task<object> ImportAsync(string jsonPath);
}
