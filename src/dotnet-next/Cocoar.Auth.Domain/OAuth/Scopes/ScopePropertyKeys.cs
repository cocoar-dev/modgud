namespace Cocoar.Auth.Domain.OAuth.Scopes;

/// <summary>Custom property keys stored on scope Properties (JSON-element values).</summary>
public static class ScopePropertyKeys
{
    public const string Enabled = "cocoar:enabled";
    public const string Required = "cocoar:required";
    public const string Emphasize = "cocoar:emphasize";
    public const string ShowInDiscoveryDocument = "cocoar:show_in_discovery_document";
    public const string UserClaims = "cocoar:user_claims";
}

/// <summary>Constants for the standard OpenID Connect scopes seeded into every realm.</summary>
public static class StandardScopes
{
    public const string OpenId = "openid";
    public const string Email = "email";
    public const string Profile = "profile";
    public const string Phone = "phone";
    public const string Address = "address";
    public const string Roles = "roles";
    public const string OfflineAccess = "offline_access";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        OpenId, Email, Profile, Phone, Address, Roles, OfflineAccess,
    };

    public static bool IsStandard(string? name) =>
        name is not null && All.Contains(name);
}
