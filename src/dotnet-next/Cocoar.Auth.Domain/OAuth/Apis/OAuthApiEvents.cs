namespace Cocoar.Auth.Domain.OAuth.Apis;

public record OAuthApiCreated(
    Guid ApiId,
    string Name,
    string? DisplayName,
    string? Description,
    bool Enabled,
    IReadOnlyList<string> Scopes);

public record OAuthApiDisplayNameChanged(Guid ApiId, string? DisplayName);
public record OAuthApiDescriptionChanged(Guid ApiId, string? Description);
public record OAuthApiEnabled(Guid ApiId);
public record OAuthApiDisabled(Guid ApiId);
public record OAuthApiScopesChanged(Guid ApiId, IReadOnlyList<string> Scopes);
public record OAuthApiUserClaimsChanged(Guid ApiId, IReadOnlyList<string> UserClaims);
public record OAuthApiPropertiesChanged(Guid ApiId, IReadOnlyDictionary<string, object?> Properties);
public record OAuthApiDeleted(Guid ApiId);
