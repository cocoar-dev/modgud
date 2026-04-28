namespace Cocoar.Auth.Authentication;

public interface IDemoSeedService
{
    Task<object> ImportAsync(string jsonPath);
}
