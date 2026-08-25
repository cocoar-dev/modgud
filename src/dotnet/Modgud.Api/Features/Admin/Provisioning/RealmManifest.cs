using System.ComponentModel;
using System.Text.Json;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.Positions;
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

    [Description("External login providers (OIDC/SAML federation). The built-in Internal provider is seeded automatically and cannot be declared here. Slug is the natural key; Type and Flavor are immutable after create.")]
    public List<RealmManifestLoginProvider> LoginProviders { get; init; } = [];

    [Description("Position principals (shared-terminal staffing identities). Requires the PositionTerminals feature flag. AccountName is the natural key. Terminal SLOTS (device enrollments + their OAuth clients) are credential material and are NOT modelled — provision them via the position/terminal admin APIs after import.")]
    public List<RealmManifestPosition> Positions { get; init; } = [];
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

    [Description("Optional per-App settings override (ADR-0011): Origin (host→app routing subdomain), Branding, PageTheme, EmailBranding, LoginExperience, SelfRegistration, NativeGrants, ClientSessions, DCR, CIMD, RegistrationFields, ChangeFeed. Patch semantics — only the sections you include change; omit to keep the App inheriting the realm settings.")]
    public ApplicationSettingsDto? Settings { get; init; }
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

    [Description("Optional access token format: 'Jwt' (self-contained) or 'Reference' (opaque, introspected). Omit = no change on apply / default 'Reference' on create.")]
    public string? AccessTokenType { get; init; }

    [Description("Optional OpenIddict consent type: 'explicit', 'implicit', 'external' or 'systematic'. Omit = no change / default 'implicit' on create.")]
    public string? ConsentType { get; init; }

    [Description("Browser origins allowed to call the token endpoint cross-origin (CORS).")]
    public List<string> AllowedCorsOrigins { get; init; } = [];

    [Description("Optional. RFC 9126: this client MUST use Pushed Authorization Requests. Omit = no change / default false.")]
    public bool? RequirePushedAuthorizationRequests { get; init; }

    [Description("Optional. RFC 9449: this client MUST present a DPoP proof at the token endpoint. Omit = no change / default false.")]
    public bool? RequireDpop { get; init; }

    [Description("Optional. RFC 9449 §8-9: DPoP proofs MUST carry a server-issued nonce. Omit = no change / default false.")]
    public bool? RequireDpopNonce { get; init; }

    [Description("Optional. Allow access tokens to be delivered via the browser. Omit = no change / default false.")]
    public bool? AllowAccessTokensViaBrowser { get; init; }

    [Description("Optional. Require the client secret at the token endpoint. Omit = no change / default true.")]
    public bool? RequireClientSecret { get; init; }

    [Description("Optional. Allow local (username/password) login for this client. Omit = no change / default true.")]
    public bool? EnableLocalLogin { get; init; }

    [Description("Optional. Allow the user to persist their consent decision. Omit = no change / default true.")]
    public bool? AllowRememberConsent { get; init; }

    [Description("Optional identity token lifetime in SECONDS. Omit = no change / provider default. Cannot be cleared via manifest — use the admin API.")]
    public int? IdentityTokenLifetime { get; init; }

    [Description("Optional access token lifetime in SECONDS. Omit = no change / provider default. Cannot be cleared via manifest — use the admin API.")]
    public int? AccessTokenLifetime { get; init; }

    [Description("Optional authorization code lifetime in SECONDS. Omit = no change / provider default.")]
    public int? AuthorizationCodeLifetime { get; init; }

    [Description("Optional sliding refresh token lifetime in SECONDS. Omit = no change / provider default.")]
    public int? SlidingRefreshTokenLifetime { get; init; }

    [Description("Optional client session idle lifetime in SECONDS. Omit = no change / realm policy default.")]
    public int? ClientSessionIdleLifetime { get; init; }

    [Description("Optional client session absolute lifetime in SECONDS. Omit = no change / realm policy default.")]
    public int? ClientSessionAbsoluteLifetime { get; init; }

    [Description("Static claims stamped onto this client's tokens. A non-empty list replaces the stored set on apply; empty/omitted = no change.")]
    public List<RealmManifestClientClaim> Claims { get; init; } = [];

    [Description("Optional prefix prepended to the client claim types. Omit = no change / no prefix on create.")]
    public string? ClientClaimsPrefix { get; init; }

    [Description("Optional. Always attach the client claims, even on user-flow tokens. Omit = no change / default false.")]
    public bool? AlwaysSendClientClaims { get; init; }

    [Description("Optional. Re-evaluate access-token claims on refresh. Omit = no change / default false.")]
    public bool? UpdateAccessTokenClaimsOnRefresh { get; init; }
}

/// <summary>A static claim stamped onto a client's tokens.</summary>
[Description("A static claim (Type + Value) stamped onto the client's tokens.")]
public sealed record RealmManifestClientClaim(
    [property: Description("Claim type, e.g. 'tenant'.")] string Type,
    [property: Description("Claim value.")] string Value);

/// <summary>A role. <see cref="App"/> is a slug; <see cref="Permissions"/> resolve into the linked app's catalog. <see cref="Key"/> (default <see cref="Name"/>) is how groups reference it.</summary>
public sealed record RealmManifestRole
{
    [Description("Optional stable key groups use to reference this role. Defaults to Name.")]
    public string? Key { get; init; }

    [Description("Role name — the natural key for upsert.")]
    public required string Name { get; init; }

    [Description("Optional description.")]
    public string? Description { get; init; }

    [Description("App slug whose catalog Permissions resolve into. Required for an App role; forbidden for a realm-admin role.")]
    public string? App { get; init; }

    [Description("If true, this role confers realm:admin across every App in this realm. App and Permissions must both be omitted.")]
    public bool IsRealmAdmin { get; init; }

    [Description("Permissions from the linked App's catalog this role grants. Requires App and is forbidden for a realm-admin role.")]
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

/// <summary>An external login provider (OIDC/SAML). <see cref="Slug"/> is the natural key.</summary>
public sealed record RealmManifestLoginProvider
{
    [Description("URL-stable identifier — the natural key (appears in /signin-oidc/{slug} and /saml/{slug}/... URLs). Immutable after create.")]
    public required string Slug { get; init; }

    [Description("Provider type: 'Oidc' (default) or 'Saml'. 'Internal' is reserved (seeded automatically). Immutable after create.")]
    public string? Type { get; init; }

    [Description("Flavor key, e.g. 'generic-oidc', 'entra-id', or a SAML flavor. Owns the FlavorData shape. Immutable after create.")]
    public required string Flavor { get; init; }

    [Description("Admin-facing name + login-page button label.")]
    public required string DisplayName { get; init; }

    [Description("Optional description shown on admin screens.")]
    public string? Description { get; init; }

    [Description("Optional. Enable the provider on the login page. Omit = no change / flavor default on create. Enabling validates readiness (e.g. SAML metadata present).")]
    public bool? Enabled { get; init; }

    [Description("OAuth client_id issued by the upstream IdP (OIDC).")]
    public string? ClientId { get; init; }

    [Description("Optional client secret from the upstream IdP. At create it is stored (encrypted); on apply to an EXISTING provider a non-empty value ROTATES the stored secret (mirrors user Password semantics). Never exported.")]
    public string? ClientSecret { get; init; }

    [Description("OIDC scopes to request upstream. Non-empty replaces; empty/omitted = no change / flavor default on create.")]
    public List<string> Scopes { get; init; } = [];

    [Description("Flavor-specific config object (e.g. { \"TenantId\": ... } for Entra, { \"MetadataUri\": ... } for generic OIDC, SAML metadata fields). Shape is owned by the Flavor.")]
    public JsonElement? FlavorData { get; init; }

    [Description("JsEval user-update script '(claims) => ({ firstname, lastname, email, acronym })' run on every login through this provider. Omit = no change / flavor default on create.")]
    public string? UserUpdateScript { get; init; }

    [Description("Optional. Persist the raw IdP claims alongside each login (PII-sensitive). Omit = no change / flavor default on create.")]
    public bool? StoreRawClaims { get; init; }

    [Description("Optional retention cap in days for the raw-claims snapshot. Omit = no change / keep-forever on create.")]
    public int? RawClaimsRetentionDays { get; init; }

    [Description("Optional. Auto-create a Modgud user for an unseen subject (JIT provisioning). Omit = no change / flavor default on create.")]
    public bool? AutoCreateUsers { get; init; }

    [Description("Optional. Allow users to link this provider from their profile. Omit = no change / default true on create.")]
    public bool? AllowLinking { get; init; }

    [Description("Optional. DANGEROUS: auto-link an unseen subject to an existing user by matching email. Enable only for tenant-controlled enterprise IdPs. Omit = no change / default false.")]
    public bool? TrustForEmailLink { get; init; }

    [Description("Optional. Federation: this provider's claims may drive externally-drivable group membership at login. Omit = no change / default false.")]
    public bool? TrustForAuthorization { get; init; }

    [Description("Optional. Federation: this provider is authoritative for the four profile fields. Omit = no change / default false.")]
    public bool? AuthoritativeForProfile { get; init; }

    [Description("Optional email-domain allowlist (e.g. ['acme.com']). Non-empty replaces; empty/omitted = no change / no filter on create.")]
    public List<string>? AllowedEmailDomains { get; init; }

    [Description("Optional login-button icon name.")]
    public string? IconName { get; init; }

    [Description("Optional login-button color (hex).")]
    public string? ButtonColorHex { get; init; }
}

/// <summary>A position principal (MG-FT). <see cref="AccountName"/> is the natural key;
/// <see cref="Grants"/> are user keys.</summary>
public sealed record RealmManifestPosition
{
    [Description("Account name — the natural key (2-64 chars, lowercase letters/digits/dots/hyphens/underscores, starts with a letter or digit). Shares the account-name namespace with users and service accounts.")]
    public required string AccountName { get; init; }

    [Description("Optional purpose/description of the position.")]
    public string? Purpose { get; init; }

    [Description("Optional. Omit = no change / default true on create. Deactivating on apply revokes the position's outstanding tokens and ends its running staffing sessions.")]
    public bool? IsActive { get; init; }

    [Description("Optional partial terminal policy (patch semantics: omitted fields keep the stored/default value). Omitted entirely = terminal use stays disabled on create / unchanged on apply. Tightening the policy on apply ends affected staffing sessions (declarative apply auto-confirms the consequences).")]
    public PositionTerminalPolicyUpdateDto? TerminalPolicy { get; init; }

    [Description("User keys (RealmManifestUser.Key — NOT ids) authorized to staff this position. Non-empty replaces the live grant set (missing grants are issued, absent ones revoked — revoking ends that user's running shifts); empty/omitted = no change.")]
    public List<string> Grants { get; init; } = [];
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
