using System.ComponentModel;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Features.Admin.Provisioning;

// The [Description] attributes below are the field-level documentation: they are
// emitted into the JSON Schema served at GET /api/admin/realms/manifest-schema, so a
// consumer (or an agent) can fetch the contract and build a valid manifest without
// reading the source. Keep them concise and accurate — they ARE the docs.

/// <summary>
/// A declarative description of a realm's complete configuration, applied in-process by
/// <see cref="RealmManifestApplier"/>. Cross-references use stable KEYS (apps by slug,
/// roles/users by key, permissions by <c>resource:action</c>) — never server-generated
/// ids — mirroring the existing <c>demo-seed.json</c> contract; the applier resolves
/// them to ids as it creates entities in dependency order. Each section maps onto the
/// SAME canonical operation the admin UI/API uses, so the manifest path and the manual
/// path can never diverge.
/// </summary>
[Description("A complete, declarative realm configuration. POST to /api/admin/realms/import to create a new realm, or to /{slug}/apply to merge into an existing one (add ?prune=true for a full sync that also deletes entities absent from the manifest). Cross-references use stable keys (app slug, role/user key, permission 'resource:action'), never server ids.")]
public sealed record RealmManifest
{
    /// <summary>Realm shell + initial admin (reuses <see cref="CreateRealmDto"/>).</summary>
    [Description("REQUIRED. The realm shell (slug, display name, routing domains) and its first admin.")]
    public required CreateRealmDto Realm { get; init; }

    /// <summary>Optional realm settings patch (self-registration, native grants, ...).</summary>
    [Description("Optional. Realm-settings patch (self-registration, registration fields, native grants, branding, auth rate limits, deletion, audit, DCR, CIMD). Omit to keep defaults; only the sections/fields you include are changed. Mirrors the realm-settings PATCH shape.")]
    public UpdateRealmSettingsDto? Settings { get; init; }

    [Description("Apps. Each app is a permission namespace: a catalog of 'resource:action' permissions plus a display name. APIs, scopes, clients and roles reference an app by its Slug.")]
    public List<RealmManifestApp> Apps { get; init; } = [];

    [Description("OAuth resource servers (APIs). The 'aud' value clients request is the API's Name.")]
    public List<RealmManifestApi> Apis { get; init; } = [];

    [Description("OAuth scopes (consent/authorization scopes), optionally linked to an app + API audiences.")]
    public List<RealmManifestScope> Scopes { get; init; } = [];

    [Description("OAuth clients (applications that request tokens). Confidential clients get a generated secret returned at import.")]
    public List<RealmManifestClient> Clients { get; init; } = [];

    [Description("Roles (named permission sets). Either app-scoped (App + Permissions) or a pure realm-admin role (IsRealmAdmin=true).")]
    public List<RealmManifestRole> Roles { get; init; } = [];

    [Description("Users. Created passwordless unless a Password is given. Referenced by groups via Key.")]
    public List<RealmManifestUser> Users { get; init; } = [];

    [Description("Groups. The ONLY way users get roles: a user is a group member, the group carries roles. Members/Roles are keys, not ids.")]
    public List<RealmManifestGroup> Groups { get; init; } = [];
}

/// <summary>A permission catalog entry referenced by <c>resource:action</c>.</summary>
[Description("A permission catalog entry, addressed elsewhere as 'resource:action' (e.g. 'invoice:read'). Both segments must match ^[a-z0-9-]+$. 'realm:admin' is reserved and cannot be a catalog entry (use a role's IsRealmAdmin flag).")]
public sealed record RealmManifestPermission(
    [property: Description("Resource segment, e.g. 'invoice'. ^[a-z0-9-]+$.")] string Resource,
    [property: Description("Action segment, e.g. 'read'. ^[a-z0-9-]+$.")] string Action,
    [property: Description("Optional human-readable description of the permission.")] string? Description = null);

/// <summary>An App + its permission catalog (the per-app permission namespace).</summary>
public sealed record RealmManifestApp
{
    [Description("Stable key for this app: 3-63 chars, lowercase letters/digits/hyphens, starts with a letter. APIs/scopes/clients/roles reference the app by this Slug.")]
    public required string Slug { get; init; }

    [Description("Human-readable app name.")]
    public required string DisplayName { get; init; }

    [Description("Optional description.")]
    public string? Description { get; init; }

    [Description("The app's permission catalog — the set of 'resource:action' permissions roles/APIs can grant from this app.")]
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

/// <summary>An OAuth resource server (API). <see cref="App"/> is a slug; <see cref="Permissions"/> resolve into the linked app's catalog.</summary>
public sealed record RealmManifestApi
{
    [Description("The API's audience ('aud') — the natural key. This is what clients request and resource servers validate.")]
    public required string Name { get; init; }

    [Description("Optional display name.")]
    public string? DisplayName { get; init; }

    [Description("Optional description.")]
    public string? Description { get; init; }

    [Description("Optional app slug this API belongs to. Required if Permissions are set (they resolve into this app's catalog).")]
    public string? App { get; init; }

    [Description("Scope names this API accepts.")]
    public List<string> Scopes { get; init; } = [];

    [Description("Permissions from the linked app's catalog this API exposes (requires App).")]
    public List<RealmManifestPermission> Permissions { get; init; } = [];

    [Description("OIDC user claims this API wants surfaced.")]
    public List<string> UserClaims { get; init; } = [];

    // Bool flags are nullable so an apply can patch surgically: omitted = no change on
    // update (and the shipped default on create). Enabled defaults to true on create.
    [Description("Optional. Omit = no change on apply / default true on create.")]
    public bool? Enabled { get; init; }

    [Description("Optional. Allow dynamic client registration (DCR) against this API. Omit = no change / default false on create.")]
    public bool? AllowDynamicRegistration { get; init; }
}

/// <summary>An OAuth scope. <see cref="App"/> is a slug; <see cref="Resources"/> are API audience names.</summary>
public sealed record RealmManifestScope
{
    [Description("Scope name — the natural key (e.g. 'invoice.read', 'openid').")]
    public required string Name { get; init; }

    [Description("Optional display name shown on the consent screen.")]
    public string? DisplayName { get; init; }

    [Description("Optional description shown on the consent screen.")]
    public string? Description { get; init; }

    [Description("Optional app slug this scope belongs to.")]
    public string? App { get; init; }

    [Description("API audience names ('aud') this scope grants access to.")]
    public List<string> Resources { get; init; } = [];

    [Description("OIDC user claims this scope releases.")]
    public List<string> UserClaims { get; init; } = [];

    // Nullable for surgical patching: omitted = no change on update / shipped default on
    // create (Enabled + ShowInDiscoveryDocument default true, the rest false).
    [Description("Optional. Omit = no change / default true on create.")]
    public bool? Enabled { get; init; }

    [Description("Optional. Scope is always granted (cannot be deselected on consent). Omit = no change / default false.")]
    public bool? Required { get; init; }

    [Description("Optional. Emphasize on the consent screen. Omit = no change / default false.")]
    public bool? Emphasize { get; init; }

    [Description("Optional. List the scope in the discovery document. Omit = no change / default true.")]
    public bool? ShowInDiscoveryDocument { get; init; }
}

/// <summary>An OAuth client. <see cref="Apps"/> are slugs; <see cref="Scopes"/> are scope names.</summary>
public sealed record RealmManifestClient
{
    [Description("The OAuth client_id — the natural key.")]
    public required string ClientId { get; init; }

    [Description("Optional display name.")]
    public string? DisplayName { get; init; }

    [Description("'confidential' (server-side; a secret is generated and returned at import) or 'public' (SPA/native; PKCE, no secret).")]
    public required string ClientType { get; init; }

    [Description("Optional explicit secret for a confidential client. Usually omit and let the server generate one (returned in the import result's ClientSecrets). Never set at apply — existing clients keep their secret.")]
    public string? ClientSecret { get; init; }

    [Description("Allowed redirect URIs (authorization_code flow).")]
    public List<string> RedirectUris { get; init; } = [];

    [Description("Allowed post-logout redirect URIs.")]
    public List<string> PostLogoutRedirectUris { get; init; } = [];

    [Description("Scope names this client may request (e.g. 'openid', 'invoice.read').")]
    public List<string> Scopes { get; init; } = [];

    [Description("OAuth grant types, e.g. 'authorization_code', 'refresh_token', 'client_credentials'.")]
    public List<string> AllowedGrantTypes { get; init; } = [];

    [Description("App slugs this client is bound to (which permission namespaces it operates in).")]
    public List<string> Apps { get; init; } = [];

    [Description("Role names granted to this client itself (e.g. for client_credentials/service-to-service).")]
    public List<string> Roles { get; init; } = [];

    [Description("Optional WebAuthn Relying Party id (passkeys) for this client.")]
    public string? WebAuthnRpId { get; init; }

    // Nullable for surgical patching: omitted = no change on update / shipped default on
    // create (Enabled defaults true, RequireConsent false).
    [Description("Optional. Omit = no change / default true on create.")]
    public bool? Enabled { get; init; }

    [Description("Optional. Force the consent screen even for first-party clients. Omit = no change / default false.")]
    public bool? RequireConsent { get; init; }

    [Description("Optional access token format: 'Jwt' (self-contained) or reference (default). Omit for the server default.")]
    public string? AccessTokenType { get; init; }
}

/// <summary>A role. <see cref="App"/> is a slug; <see cref="Permissions"/> resolve into the linked app's catalog. <see cref="Key"/> (default <see cref="Name"/>) is how groups reference it.</summary>
public sealed record RealmManifestRole
{
    [Description("Optional stable key groups use to reference this role. Defaults to Name.")]
    public string? Key { get; init; }

    [Description("Role name — the natural key for upsert.")]
    public required string Name { get; init; }

    [Description("Optional description.")]
    public string? Description { get; init; }

    [Description("App slug whose catalog Permissions resolve into. Omit for a pure realm-admin role.")]
    public string? App { get; init; }

    [Description("If true, this role confers realm:admin — the realm-wide bypass (full administration). A realm-admin role needs no App/Permissions. Provisioning is trusted, so this is allowed from the manifest.")]
    public bool IsRealmAdmin { get; init; }

    [Description("Permissions from the linked app's catalog this role grants (requires App).")]
    public List<RealmManifestPermission> Permissions { get; init; } = [];

    public string ResolveKey() => Key ?? Name;
}

/// <summary>A user. <see cref="Key"/> (default <see cref="UserName"/> ?? <see cref="Email"/>) is how groups reference it as a member.</summary>
public sealed record RealmManifestUser
{
    [Description("Optional stable key groups use to reference this user as a member. Defaults to UserName, else Email.")]
    public string? Key { get; init; }

    [Description("Optional first name.")]
    public string? Firstname { get; init; }

    [Description("Optional last name.")]
    public string? Lastname { get; init; }

    [Description("Optional short acronym/initials.")]
    public string? Acronym { get; init; }

    [Description("Email — the user's natural key (also the login identifier when no UserName is set).")]
    public required string Email { get; init; }

    [Description("Optional username. Falls back to the email local-part if omitted.")]
    public string? UserName { get; init; }

    [Description("Optional password. Omit to create the user passwordless (set one later, or use a passwordless flow). On apply, a password on an EXISTING user updates it.")]
    public string? Password { get; init; }

    [Description("Mark the email as already verified. Default false.")]
    public bool EmailConfirmed { get; init; }

    public string ResolveKey() => Key ?? UserName ?? Email;
}

/// <summary>A group. <see cref="Members"/> are user keys; <see cref="Roles"/> are role keys.</summary>
public sealed record RealmManifestGroup
{
    [Description("Group name — the natural key.")]
    public required string Name { get; init; }

    [Description("Optional description.")]
    public string? Description { get; init; }

    [Description("Member user keys (RealmManifestUser.Key — NOT ids). For MembershipMode=Manual.")]
    public List<string> Members { get; init; } = [];

    [Description("Role keys (RealmManifestRole.Key/Name — NOT ids) this group grants to its members.")]
    public List<string> Roles { get; init; } = [];

    [Description("'Manual' (explicit Members) or 'Auto' (members computed from MembershipScript). Default 'Manual'.")]
    public string MembershipMode { get; init; } = "Manual";

    [Description("For MembershipMode=Auto: a TypeScript membership predicate. Ignored for Manual.")]
    public string? MembershipScript { get; init; }

    [Description("Optional shared group email.")]
    public string? Email { get; init; }

    [Description("'Shared' or 'Individual'. Default 'Shared'.")]
    public string EmailMode { get; init; } = "Shared";

    [Description("App slugs this group's roles apply to. Omit -> defaults to ['modgud'] (the IdP itself). An empty list makes the group dormant (its roles confer nothing).")]
    public List<string>? BoundTo { get; init; }

    [Description("Allow an external IdP (federation) to drive this group's membership. A realm:admin-conferring group can never be externally drivable. Default false.")]
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
