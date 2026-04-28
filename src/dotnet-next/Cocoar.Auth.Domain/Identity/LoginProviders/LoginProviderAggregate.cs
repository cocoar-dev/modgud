namespace Cocoar.Auth.Domain.Identity.LoginProviders;

/// <summary>
/// Event-sourced aggregate for a login provider configuration. Login providers
/// represent authentication methods (Internal password, OpenID Connect external IdPs).
/// </summary>
public class LoginProviderAggregate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string? Description { get; private set; }
    public LoginProviderType Type { get; private set; }
    public Dictionary<string, string> Configuration { get; private set; } = new();
    public bool IsBuiltIn { get; private set; }
    public bool IsDeleted { get; private set; }

    public LoginProviderAggregate() { }

    public static (LoginProviderAggregate, LoginProviderCreated) Create(
        Guid id,
        string name,
        string? displayName,
        string? description,
        LoginProviderType type,
        Dictionary<string, string> configuration,
        bool isBuiltIn)
    {
        var aggregate = new LoginProviderAggregate();
        var e = new LoginProviderCreated(id, name, displayName, description, type, configuration, isBuiltIn);
        aggregate.Apply(e);
        return (aggregate, e);
    }

    public LoginProviderNameChanged SetName(string name) { var e = new LoginProviderNameChanged(Id, name); Apply(e); return e; }
    public LoginProviderDisplayNameChanged SetDisplayName(string? v) { var e = new LoginProviderDisplayNameChanged(Id, v); Apply(e); return e; }
    public LoginProviderDescriptionChanged SetDescription(string? v) { var e = new LoginProviderDescriptionChanged(Id, v); Apply(e); return e; }
    public LoginProviderConfigurationChanged SetConfiguration(Dictionary<string, string> cfg) { var e = new LoginProviderConfigurationChanged(Id, cfg); Apply(e); return e; }
    public LoginProviderDeleted Delete() { var e = new LoginProviderDeleted(Id); Apply(e); return e; }

    public void Apply(LoginProviderCreated e)
    {
        Id = e.LoginProviderId; Name = e.Name; DisplayName = e.DisplayName; Description = e.Description;
        Type = e.Type; Configuration = new Dictionary<string, string>(e.Configuration); IsBuiltIn = e.IsBuiltIn;
    }
    public void Apply(LoginProviderNameChanged e) => Name = e.NewName;
    public void Apply(LoginProviderDisplayNameChanged e) => DisplayName = e.NewDisplayName;
    public void Apply(LoginProviderDescriptionChanged e) => Description = e.NewDescription;
    public void Apply(LoginProviderConfigurationChanged e) => Configuration = new Dictionary<string, string>(e.NewConfiguration);
    public void Apply(LoginProviderDeleted e) => IsDeleted = true;
}
