namespace Modgud.Application.DTOs.OAuth;

public record ApiSecretEntryDto
{
    public required string SecretId { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? Expiration { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record OAuthApiDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public required List<string> Scopes { get; init; }
    public required List<string> UserClaims { get; init; }
    /// <summary>
    /// FK to <c>App.Id</c> (Guid string). Null = unassigned (this RS exists
    /// but cannot authenticate against the distribution API yet).
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Subset of the linked App's permission catalog this RS gates on.
    /// Each entry is an <c>AppPermission.Id</c> (Guid string) FK into
    /// <c>App.Permissions</c>. Empty list means the RS doesn't gate on
    /// anything yet.
    /// </summary>
    public List<string> PermissionIds { get; init; } = new();

    public List<ApiSecretEntryDto> Secrets { get; init; } = new();

    /// <summary>
    /// <c>true</c> when a sibling <see cref="OAuthScopeDto"/> with the same
    /// <c>Name</c> already exists. Drives the admin-UI affordance "Create
    /// implicit scope" — hidden when the API already has a 1:1 scope wired
    /// up. The check is "name match", not "linked-to-this-api", because
    /// scope-name uniqueness is realm-global and the implicit-scope
    /// convention is `scope.Name == api.Name`.
    /// </summary>
    public bool HasImplicitScope { get; init; }

    /// <summary>
    /// When <c>true</c>, this resource server is a valid <c>resource=</c>
    /// target for clients minted via Dynamic Client Registration (RFC 7591).
    /// Off by default — every RS has to be explicitly opted in. One half of
    /// the triple opt-in (realm master + per-Api flag + per-Scope flag).
    /// </summary>
    public bool AllowDynamicRegistration { get; init; }
}

public record CreateOAuthApiDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
    public List<string> UserClaims { get; init; } = new();
    /// <summary>
    /// App.Id (Guid string) the resource server belongs to. Null = leave
    /// unassigned for now (must be set later before the RS can authenticate
    /// against the distribution API).
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Optional initial subset of the linked App's catalog. Each entry is
    /// an <c>AppPermission.Id</c> (Guid string). Validated against the
    /// linked App's catalog at create time. Ignored when <see cref="AppId"/>
    /// is null (rejected as a validation error if non-empty without an
    /// AppId).
    /// </summary>
    public List<string> PermissionIds { get; init; } = new();

    /// <summary>Per-API DCR opt-in. Default <c>false</c>; admin flips on
    /// before publishing the RS as a DCR-target.</summary>
    public bool AllowDynamicRegistration { get; init; }
}

public record UpdateOAuthApiDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public List<string>? Scopes { get; init; }
    public List<string>? UserClaims { get; init; }
    /// <summary>
    /// PATCH semantics: null/missing = no change, "" = detach (mark
    /// unassigned), "<guid>" = assign or change.
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// PATCH semantics: null/missing = no change, empty list = clear.
    /// Each entry is an <c>AppPermission.Id</c> (Guid string), validated
    /// against the linked App's catalog. Detaching the App (AppId = "")
    /// in the same payload requires PermissionIds to be empty or absent.
    /// </summary>
    public List<string>? PermissionIds { get; init; }

    /// <summary>PATCH semantics: null = no change.</summary>
    public bool? AllowDynamicRegistration { get; init; }
}

public record OAuthApiListDto
{
    public required List<OAuthApiDto> Items { get; init; }
    public int TotalCount { get; init; }
}

public record OAuthApiCreatedDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public required List<string> Scopes { get; init; }
    public required List<string> UserClaims { get; init; }

    public required string ApiSecret { get; init; }
}

public record ApiSecretDto
{
    public required string ApiSecret { get; init; }
}

public record CreateApiSecretDto
{
    public string Type { get; init; } = "SharedSecret";
    public string? Description { get; init; }
    public DateTimeOffset? Expiration { get; init; }
}

public record ApiSecretCreatedDto
{
    public required string SecretId { get; init; }
    public required string ApiSecret { get; init; }
}

public record PaginationRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// Builds a request from raw query-string ints, clamping non-positive values
    /// to the same defaults as the parameterless constructor (1 and 20). Use
    /// this from endpoints where <c>?page=</c> / <c>?pageSize=</c> are absent
    /// (binding to 0) or negative — both should land on page 1 with 20 rows.
    /// </summary>
    public static PaginationRequest WithDefaults(int page, int pageSize)
        => new() { Page = page <= 0 ? 1 : page, PageSize = pageSize <= 0 ? 20 : pageSize };
}
