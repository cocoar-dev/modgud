namespace Cocoar.Auth.Client.AspNetCore.Distribution;

/// <summary>
/// Wire shape of <c>GET /api/v1/distribution/me-permissions</c>. Mirrors
/// <c>Cocoar.Auth.Api.Features.Distribution.MePermissionsResponse</c> on
/// the server side. PascalCase fields — System.Text.Json is configured
/// case-insensitive at the HttpClient level so this also accepts
/// camelCase server JSON without ceremony.
/// </summary>
public sealed record MePermissionsResponse(
    string UserId,
    string AppSlug,
    string[] Permissions,
    GroupRef[] Groups,
    RoleRef[] Roles);

public sealed record GroupRef(string Id, string Name);
public sealed record RoleRef(string Id, string Name);
