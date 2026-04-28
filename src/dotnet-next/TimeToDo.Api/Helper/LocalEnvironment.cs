namespace TimeToDo.Api.Helper;

public class LocalEnvironment
{
    private static readonly Lazy<LocalEnvironment> _instance = new Lazy<LocalEnvironment>(() => new LocalEnvironment());
    public static LocalEnvironment Instance = _instance.Value;

    public bool IsDevelopment => Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
}