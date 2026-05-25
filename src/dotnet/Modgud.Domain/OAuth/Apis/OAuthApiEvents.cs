namespace Modgud.Domain.OAuth.Apis;

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

/// <summary>
/// Sets the App this resource-server belongs to. <c>null</c> = unassigned
/// (the RS exists but <c>/connect/userinfo</c> won't emit a per-Audience
/// <c>resource_access</c> block for it). Realm-admin endpoints validate
/// that the AppId resolves to a non-deleted <c>App</c> at append time.
/// </summary>
public record OAuthApiAppIdChanged(Guid ApiId, Guid? AppId);

/// <summary>
/// Replaces the RS's catalog subset (the set of permissions it gates on)
/// with the given <c>AppPermission.Id</c>s. Admin endpoints validate
/// that every id resolves to an entry in the linked App's catalog before
/// appending — a stale id never lands in the stream.
/// </summary>
public record OAuthApiPermissionIdsChanged(Guid ApiId, IReadOnlyList<Guid> PermissionIds);

public record OAuthApiDeleted(Guid ApiId);
