namespace Cocoar.Auth.Domain.Identity.LoginProviders;

public record LoginProviderCreated(
    Guid LoginProviderId,
    string Name,
    string? DisplayName,
    string? Description,
    LoginProviderType Type,
    Dictionary<string, string> Configuration,
    bool IsBuiltIn);

public record LoginProviderNameChanged(Guid LoginProviderId, string NewName);
public record LoginProviderDisplayNameChanged(Guid LoginProviderId, string? NewDisplayName);
public record LoginProviderDescriptionChanged(Guid LoginProviderId, string? NewDescription);
public record LoginProviderConfigurationChanged(Guid LoginProviderId, Dictionary<string, string> NewConfiguration);
public record LoginProviderDeleted(Guid LoginProviderId);

public enum LoginProviderType
{
    /// <summary>Built-in password-based authentication.</summary>
    Internal = 0,

    /// <summary>External OpenID Connect provider.</summary>
    OpenIdConnect = 1
}
