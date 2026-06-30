using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// A declarative description of a realm's complete configuration, applied in-process by
/// <see cref="RealmManifestApplier"/>. Cross-references use stable KEYS (apps by slug,
/// roles/users by key, permissions by <c>resource:action</c>) — never server-generated
/// ids — mirroring the existing <c>demo-seed.json</c> contract; the applier resolves
/// them to ids as it creates entities in dependency order. Each section maps onto the
/// SAME canonical operation the admin UI/API uses, so the manifest path and the manual
/// path can never diverge.
/// </summary>
public sealed record RealmManifest
{
    /// <summary>Realm shell + initial admin (reuses <see cref="CreateRealmDto"/>).</summary>
    public required CreateRealmDto Realm { get; init; }

    /// <summary>Optional realm settings patch (self-registration, native grants, ...).</summary>
    public UpdateRealmSettingsDto? Settings { get; init; }

    public List<RealmManifestApp> Apps { get; init; } = [];
    public List<RealmManifestApi> Apis { get; init; } = [];
    public List<RealmManifestScope> Scopes { get; init; } = [];
    public List<RealmManifestClient> Clients { get; init; } = [];
    public List<RealmManifestRole> Roles { get; init; } = [];
    public List<RealmManifestUser> Users { get; init; } = [];
    public List<RealmManifestGroup> Groups { get; init; } = [];
}

/// <summary>A permission catalog entry referenced by <c>resource:action</c>.</summary>
public sealed record RealmManifestPermission(string Resource, string Action, string? Description = null);

/// <summary>An App + its permission catalog (the per-app permission namespace).</summary>
public sealed record RealmManifestApp
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

/// <summary>An OAuth resource server (API). <see cref="App"/> is a slug; <see cref="Permissions"/> resolve into the linked app's catalog.</summary>
public sealed record RealmManifestApi
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Scopes { get; init; } = [];
    public List<RealmManifestPermission> Permissions { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];

    // Bool flags are nullable so an apply can patch surgically: omitted = no change on
    // update (and the shipped default on create). Enabled defaults to true on create.
    public bool? Enabled { get; init; }
    public bool? AllowDynamicRegistration { get; init; }
}

/// <summary>An OAuth scope. <see cref="App"/> is a slug; <see cref="Resources"/> are API audience names.</summary>
public sealed record RealmManifestScope
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Resources { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];

    // Nullable for surgical patching: omitted = no change on update / shipped default on
    // create (Enabled + ShowInDiscoveryDocument default true, the rest false).
    public bool? Enabled { get; init; }
    public bool? Required { get; init; }
    public bool? Emphasize { get; init; }
    public bool? ShowInDiscoveryDocument { get; init; }
}

/// <summary>An OAuth client. <see cref="Apps"/> are slugs; <see cref="Scopes"/> are scope names.</summary>
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

    // Nullable for surgical patching: omitted = no change on update / shipped default on
    // create (Enabled defaults true, RequireConsent false).
    public bool? Enabled { get; init; }
    public bool? RequireConsent { get; init; }
    public string? AccessTokenType { get; init; }
}

/// <summary>A role. <see cref="App"/> is a slug; <see cref="Permissions"/> resolve into the linked app's catalog. <see cref="Key"/> (default <see cref="Name"/>) is how groups reference it.</summary>
public sealed record RealmManifestRole
{
    public string? Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public bool IsRealmAdmin { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];

    public string ResolveKey() => Key ?? Name;
}

/// <summary>A user. <see cref="Key"/> (default <see cref="UserName"/> ?? <see cref="Email"/>) is how groups reference it as a member.</summary>
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

    public string ResolveKey() => Key ?? UserName ?? Email;
}

/// <summary>A group. <see cref="Members"/> are user keys; <see cref="Roles"/> are role keys.</summary>
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

/// <summary>The outcome of a successful import.</summary>
public sealed record RealmImportResult
{
    public required string Slug { get; init; }
    public required string PrimaryDomain { get; init; }

    /// <summary>
    /// Plaintext secrets of the confidential clients created during the import
    /// (clientId → secret). Secrets are only returned at create time, so they are
    /// surfaced here for a test-kit / caller to use without a separate fetch.
    /// </summary>
    public Dictionary<string, string> ClientSecrets { get; init; } = [];
}
