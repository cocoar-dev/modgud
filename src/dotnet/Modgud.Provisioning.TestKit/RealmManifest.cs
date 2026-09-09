using System.Text.Json.Nodes;

namespace Modgud.Provisioning.TestKit;

/// <summary>
/// Declarative description of a realm's complete configuration, posted to the Modgud
/// control-plane provisioning API. This is the client-side mirror of the server's manifest
/// contract — cross-references use stable KEYS (apps by slug, roles/users by key,
/// permissions by <c>resource:action</c>), never server-generated ids. The JSON shape is
/// what <c>POST /api/admin/realms/{slug}/apply</c> binds; the round-trip is exercised
/// end-to-end by the IdP repo's own provisioning tests so the two sides can't silently drift.
///
/// <para>A manifest describes CONTENT only and carries no realm shell — the target realm is
/// named by the route, so the same file applies to any realm. Create the realm separately
/// with a <see cref="RealmSpec"/> (see
/// <see cref="ModgudProvisioningClient.ImportRealmAsync"/>).</para>
/// </summary>
public sealed record RealmManifest
{
    /// <summary>Optional raw realm-settings patch (self-registration, native grants, …).
    /// Left as a free-form JSON object so the kit doesn't have to mirror the full settings
    /// surface; <c>null</c> = no settings change.</summary>
    public JsonObject? Settings { get; init; }

    public List<RealmManifestApp> Apps { get; init; } = [];
    public List<RealmManifestApi> Apis { get; init; } = [];
    public List<RealmManifestScope> Scopes { get; init; } = [];
    public List<RealmManifestClient> Clients { get; init; } = [];
    public List<RealmManifestRole> Roles { get; init; } = [];
    public List<RealmManifestUser> Users { get; init; } = [];
    public List<RealmManifestGroup> Groups { get; init; } = [];
}

/// <summary>The realm shell — the payload of <c>POST /api/admin/realms</c>. Deliberately NOT
/// part of <see cref="RealmManifest"/>: slug, routing domains and primary domain are
/// deployment identity, and keeping them out of the manifest is what makes a manifest
/// portable between realms and environments.</summary>
public sealed record RealmSpec
{
    public required string Slug { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[]? Domains { get; init; }
    public string? PrimaryDomain { get; init; }
    public InitialAdmin InitialAdmin { get; init; } = new();
}

/// <summary>The realm's first admin, issued as a pending admin invite by the create-realm
/// call. Optional — a manifest can equally provision admins outright via
/// <see cref="RealmManifest.Users"/> + <see cref="RealmManifest.Groups"/>.</summary>
public sealed record InitialAdmin
{
    public string UserName { get; init; } = "admin";
    public string Email { get; init; } = "admin@example.test";
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}

public sealed record RealmManifestPermission(string Resource, string Action, string? Description = null);

public sealed record RealmManifestApp
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

public sealed record RealmManifestApi
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Scopes { get; init; } = [];
    public List<RealmManifestPermission> Permissions { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? AllowDynamicRegistration { get; init; }
}

public sealed record RealmManifestScope
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Resources { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? Required { get; init; }
    public bool? Emphasize { get; init; }
    public bool? ShowInDiscoveryDocument { get; init; }
}

public sealed record RealmManifestClient
{
    public required string ClientId { get; init; }
    public string? DisplayName { get; init; }
    public required string ClientType { get; init; }
    public string? ClientSecret { get; init; }
    public List<string> RedirectUris { get; init; } = [];
    public List<string> PostLogoutRedirectUris { get; init; } = [];
    public List<string> Scopes { get; init; } = [];
    public List<string> AllowedGrantTypes { get; init; } = [];
    public List<string> Apps { get; init; } = [];
    public List<string> Roles { get; init; } = [];
    public string? WebAuthnRpId { get; init; }
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? RequireConsent { get; init; }
}

public sealed record RealmManifestRole
{
    public string? Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public bool IsRealmAdmin { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

public sealed record RealmManifestUser
{
    public string? Key { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? Acronym { get; init; }
    public required string Email { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool EmailConfirmed { get; init; }
}

public sealed record RealmManifestGroup
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> Members { get; init; } = [];
    public List<string> Roles { get; init; } = [];
    public string MembershipMode { get; init; } = "Manual";
    public string? MembershipScript { get; init; }
    public string? Email { get; init; }
    public string EmailMode { get; init; } = "Shared";
    public List<string>? BoundTo { get; init; }
    public bool ExternallyDrivable { get; init; }
}
