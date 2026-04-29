namespace Cocoar.Auth.Domain.OAuth.Scopes;

public record OAuthScopeCreated(
    Guid ScopeId,
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Resources);

public record OAuthScopeDisplayNameChanged(Guid ScopeId, string? DisplayName);
public record OAuthScopeDescriptionChanged(Guid ScopeId, string? Description);
public record OAuthScopeResourcesChanged(Guid ScopeId, IReadOnlyList<string> Resources);
public record OAuthScopeDisplayNamesChanged(Guid ScopeId, IReadOnlyDictionary<string, string> DisplayNames);
public record OAuthScopeDescriptionsChanged(Guid ScopeId, IReadOnlyDictionary<string, string> Descriptions);
public record OAuthScopePropertiesChanged(Guid ScopeId, IReadOnlyDictionary<string, object?> Properties);
public record OAuthScopeEnabledChanged(Guid ScopeId, bool Enabled);
public record OAuthScopeRequiredChanged(Guid ScopeId, bool Required);
public record OAuthScopeEmphasizeChanged(Guid ScopeId, bool Emphasize);
public record OAuthScopeShowInDiscoveryDocumentChanged(Guid ScopeId, bool ShowInDiscoveryDocument);
public record OAuthScopeUserClaimsChanged(Guid ScopeId, IReadOnlyList<string> UserClaims);

/// <summary>
/// Changes the scope's App-link. <c>null</c> = global scope (cross-app,
/// e.g. the standard OIDC scopes). Realm-admin endpoints validate that the
/// AppId references an existing non-deleted App at append time.
/// </summary>
public record OAuthScopeAppIdChanged(Guid ScopeId, Guid? AppId);

public record OAuthScopeDeleted(Guid ScopeId);
