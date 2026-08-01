using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Features.Roles;
using Modgud.Api.Features.Users.Commands;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.User;
using Modgud.Application.Services;
using Modgud.Authentication.Api.Users;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Authorization.Services;
using Modgud.Domain.Common;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Permissions;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Applies a <see cref="RealmManifest"/> in-process by calling the existing canonical
/// application operations — the engine behind declarative realm provisioning.
///
/// <para>Invariant: ZERO new write logic. Each section is dispatched to the SAME
/// operation the admin UI / admin API uses (<see cref="IRealmProvisioningService"/>,
/// <see cref="IRealmSettingsService"/>, <see cref="AppAdminService"/>,
/// <see cref="OAuthAdminService"/>, <see cref="RoleAdminService"/>, the user/group
/// command handlers), so the manifest path and the manual path can never drift.</para>
///
/// <para>Tenant routing: the realm shell is created via the global store, then the
/// per-tenant config runs inside <c>TenantContext.Enter(slug)</c> + a fresh DI scope —
/// <c>TenantedSessionFactory</c> prefers the AsyncLocal <c>TenantContext</c> over the
/// ambient (control-plane) <c>HttpContext</c>. Handlers resolved in that fresh scope
/// therefore write to the newly provisioned tenant.</para>
///
/// <para>Cross-references resolve in dependency order: apps → apis/scopes/clients →
/// roles → users → groups. Keys (app slug, role/user key, <c>resource:action</c>) are
/// mapped to ids as each entity is created.</para>
/// </summary>
public sealed class RealmManifestApplier(
    IRealmProvisioningService realms,
    IServiceScopeFactory scopeFactory,
    ILogger<RealmManifestApplier> logger)
{
    /// <summary>
    /// Imports a brand-new realm: the slug must NOT already exist. Provisions the realm
    /// shell (tenant DB + seed) then applies the manifest. If any step fails the whole
    /// partially-provisioned realm is hard-deleted, so a failed import leaves nothing
    /// behind (all-or-nothing).
    /// </summary>
    public async Task<ErrorOr<RealmImportResult>> ImportNewRealmAsync(
        RealmManifest manifest, CancellationToken ct = default)
    {
        var slug = manifest.Realm.Slug;

        if (await realms.GetRealmBySlugAsync(slug, ct) is not null)
            return Error.Conflict("Realm.AlreadyExists",
                $"Realm '{slug}' already exists. Use UpdateRealm to modify an existing realm.");

        var realmResult = await realms.CreateRealmAsync(manifest.Realm, ct);
        if (realmResult.IsError) return realmResult.Errors;
        var realm = realmResult.Value;

        try
        {
            var secrets = await ApplyTenantConfigAsync(slug, manifest, ct);
            logger.LogInformation(
                "Imported realm {Slug}: {Apps} apps, {Apis} apis, {Scopes} scopes, {Clients} clients, {Roles} roles, {Users} users, {Groups} groups.",
                slug, manifest.Apps.Count, manifest.Apis.Count, manifest.Scopes.Count,
                manifest.Clients.Count, manifest.Roles.Count, manifest.Users.Count, manifest.Groups.Count);
            return new RealmImportResult
            {
                Slug = slug,
                PrimaryDomain = realm.PrimaryDomain,
                ClientSecrets = secrets,
            };
        }
        catch (ManifestApplyException ex)
        {
            // A failed import must leave nothing behind: roll the whole realm back via
            // the prod-safe hard-delete (drops the tenant DB + the global record).
            logger.LogError(ex,
                "Manifest apply failed for realm {Slug} ({What}); hard-deleting the partially-provisioned realm.",
                slug, ex.What);
            await realms.HardDeleteRealmAsync(slug, ct);
            return ex.Errors;
        }
    }

    /// <summary>
    /// Updates an existing realm in place: the slug MUST already exist. Each entity in the
    /// manifest is upserted by its natural key (app slug, api/scope/role/group name, client
    /// id, user email/username) — created if absent, otherwise updated through the SAME
    /// canonical Update operation the admin API uses. The realm database is NEVER dropped
    /// (that would discard signing keys, the OpenIddict token store and user <c>sub</c>s),
    /// so this is a strict in-place merge.
    ///
    /// <para>Semantics (v1, merge/upsert — entity-level prune is a separate later stage):
    /// the manifest is the desired state for the fields it carries. Boolean flags are always
    /// applied; scalar strings and non-empty lists replace the stored value; an omitted /
    /// empty list and a null app-link leave the stored value unchanged (UpdateRealm sets and
    /// changes, but never clears a list to empty or detaches an app-link — use the admin API
    /// for that). Client secrets are only minted at create; an existing client keeps its
    /// secret (rotate via the dedicated endpoint).</para>
    ///
    /// <para>Unlike import there is no all-or-nothing rollback: each canonical op commits its
    /// own unit of work, so a mid-apply failure leaves the earlier successful writes in place.
    /// The upserts are safe to re-apply after fixing the manifest.</para>
    ///
    /// <para>When <paramref name="prune"/> is set the merge becomes a full sync (k8s
    /// <c>apply --prune</c>): after the upsert, every entity that exists in the realm but is
    /// absent from the manifest is deleted via its canonical delete op, in reverse-dependency
    /// order. Lockout- and infrastructure-protected entities are NEVER pruned — the system app,
    /// auto-seeded standard scopes, service-account-linked clients, and anything conferring
    /// <c>realm:admin</c> (a realm-admin role, any user who currently holds realm:admin, and any
    /// admin-conferring group). Without the flag the additive merge above is unchanged.</para>
    /// </summary>
    public async Task<ErrorOr<RealmImportResult>> UpdateRealmAsync(
        RealmManifest manifest, bool prune = false, CancellationToken ct = default)
    {
        var slug = manifest.Realm.Slug;

        var realm = await realms.GetRealmBySlugAsync(slug, ct);
        if (realm is null)
            return Error.NotFound("Realm.NotFound",
                $"Realm '{slug}' does not exist. Use ImportNewRealm to create it.");

        try
        {
            var secrets = await ApplyTenantUpdateAsync(slug, manifest, prune, ct);
            logger.LogInformation(
                "Updated realm {Slug}: {Apps} apps, {Apis} apis, {Scopes} scopes, {Clients} clients, {Roles} roles, {Users} users, {Groups} groups (in-place merge).",
                slug, manifest.Apps.Count, manifest.Apis.Count, manifest.Scopes.Count,
                manifest.Clients.Count, manifest.Roles.Count, manifest.Users.Count, manifest.Groups.Count);
            return new RealmImportResult
            {
                Slug = slug,
                PrimaryDomain = realm.PrimaryDomain,
                ClientSecrets = secrets,
            };
        }
        catch (ManifestApplyException ex)
        {
            // In-place update never drops the realm DB. A partial failure leaves the writes
            // that committed before it in place; surface the error so the caller can fix the
            // manifest and re-apply (every step is an idempotent upsert).
            logger.LogError(ex,
                "Manifest update failed for realm {Slug} ({What}); the realm is left partially updated.",
                slug, ex.What);
            return ex.Errors;
        }
    }

    private async Task<Dictionary<string, string>> ApplyTenantConfigAsync(
        string slug, RealmManifest manifest, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var apps = new Dictionary<string, App>(StringComparer.Ordinal);        // slug → App (id + catalog)
        var roleIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // role key → id (for groups)
        var userIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // user key → id (for groups)

        // Enter the new realm's tenant context, then resolve the per-tenant services in
        // a FRESH scope so their IDocumentSession binds to this tenant.
        using var _ = TenantContext.Enter(slug);
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        if (manifest.Settings is not null)
            EnsureOk(await sp.GetRequiredService<IRealmSettingsService>().PatchAsync(manifest.Settings, ct), "settings");

        // ── Apps (+ permission catalog) — referenced by everything below ──────────
        var appAdmin = sp.GetRequiredService<AppAdminService>();
        foreach (var app in manifest.Apps)
        {
            var dto = new CreateAppDto(app.Slug, app.DisplayName, app.Description,
                app.Permissions.Select(p => new AppPermissionDto(null, p.Resource, p.Action, p.Description)).ToList());
            var created = await appAdmin.CreateAppAsync(dto, ct);
            EnsureOk(created, $"app '{app.Slug}'");
            apps[app.Slug] = created.Value;
        }

        var oauth = sp.GetRequiredService<OAuthAdminService>();

        // ── OAuth APIs ────────────────────────────────────────────────────────────
        foreach (var api in manifest.Apis)
        {
            EnsureOk(await oauth.CreateApiAsync(new CreateOAuthApiDto
            {
                Name = api.Name,
                DisplayName = api.DisplayName,
                Description = api.Description,
                Enabled = api.Enabled ?? true,
                Scopes = api.Scopes,
                UserClaims = api.UserClaims,
                AppId = ResolveAppId(apps, api.App, $"api '{api.Name}'"),
                PermissionIds = ResolvePermissionIds(apps, api.App, api.Permissions, $"api '{api.Name}'"),
                AllowDynamicRegistration = api.AllowDynamicRegistration ?? false,
            }, ct), $"api '{api.Name}'");
        }

        // ── OAuth scopes ──────────────────────────────────────────────────────────
        foreach (var s in manifest.Scopes)
        {
            EnsureOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Name = s.Name,
                DisplayName = s.DisplayName,
                Description = s.Description,
                Resources = s.Resources,
                UserClaims = s.UserClaims,
                Enabled = s.Enabled ?? true,
                Required = s.Required ?? false,
                Emphasize = s.Emphasize ?? false,
                ShowInDiscoveryDocument = s.ShowInDiscoveryDocument ?? true,
                AppId = ResolveAppId(apps, s.App, $"scope '{s.Name}'"),
            }, ct), $"scope '{s.Name}'");
        }

        // ── OAuth clients ─────────────────────────────────────────────────────────
        foreach (var c in manifest.Clients)
        {
            var created = await oauth.CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = c.ClientId,
                DisplayName = c.DisplayName,
                ClientType = c.ClientType,
                ClientSecret = c.ClientSecret,
                RedirectUris = c.RedirectUris,
                PostLogoutRedirectUris = c.PostLogoutRedirectUris,
                Scopes = c.Scopes,
                AllowedGrantTypes = c.AllowedGrantTypes,
                Roles = c.Roles,
                WebAuthnRpId = c.WebAuthnRpId,
                Enabled = c.Enabled ?? true,
                RequireConsent = c.RequireConsent ?? false,
                AppIds = c.Apps.Count == 0
                    ? null
                    : c.Apps.Select(appSlug => ResolveAppId(apps, appSlug, $"client '{c.ClientId}'")!).ToList(),
            }, ct);
            EnsureOk(created, $"client '{c.ClientId}'");
            if (created.Value.ClientSecret is not null)
                secrets[c.ClientId] = created.Value.ClientSecret;
        }

        // ── Roles (app-scoped or realm-admin) ─────────────────────────────────────
        var roleAdmin = sp.GetRequiredService<RoleAdminService>();
        foreach (var r in manifest.Roles)
        {
            var payload = new RolePayload(
                r.Name,
                r.Description,
                ResolveAppId(apps, r.App, $"role '{r.Name}'"),
                r.IsRealmAdmin,
                ResolvePermissionIds(apps, r.App, r.Permissions, $"role '{r.Name}'"));
            // Control-plane provisioning is trusted, so the realm-admin guard is satisfied.
            var created = await roleAdmin.CreateRoleAsync(payload, callerIsRealmAdmin: true, ct);
            EnsureOk(created, $"role '{r.Name}'");
            roleIds[r.ResolveKey()] = created.Value.Id;
        }

        // ── Users — canonical handler on the manifest's tenant session ────────────
        // Realm provisioning has already initialized Wolverine's inbox/outbox.
        // Direct invocation keeps manifest application sequential and exposes the
        // canonical handler result immediately for contextual import errors.
        var userSession = sp.GetRequiredService<IDocumentSession>();
        var createUser = new CreateUserHandler(
            userSession,
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IApplicationSettingsResolver>());
        foreach (var u in manifest.Users)
        {
            var cmd = new CreateUserCommand(u.Firstname, u.Lastname, u.Acronym, u.Email,
                u.UserName ?? string.Empty, u.Password, u.EmailConfirmed);
            var created = await createUser.Handle(cmd, ct);
            EnsureOk(created, $"user '{u.Email}'");
            if (ShortGuid.TryParse(created.Value.Id, out Guid uid))
                userIds[u.ResolveKey()] = uid;
        }

        // ── Groups — canonical handler on the manifest's tenant session ───────────
        // Keep the same explicit, sequential dispatch used for users so reference
        // resolution and contextual import failures remain deterministic.
        if (manifest.Groups.Count > 0)
        {
            var groupHandler = new CreateGroupHandler(
                sp.GetRequiredService<IDocumentSession>(),
                sp.GetRequiredService<IMembershipEvaluator>(),
                sp.GetRequiredService<IAutoMembershipRecalculator>());

            foreach (var g in manifest.Groups)
            {
                var memberIds = g.Members.Select(m => ResolveRef(userIds, m, $"group '{g.Name}' member '{m}'")).ToList();
                var groupRoleIds = g.Roles.Select(rk => ResolveRef(roleIds, rk, $"group '{g.Name}' role '{rk}'")).ToList();
                var cmd = new CreateGroupCommand(
                    g.Name, g.Description, memberIds, groupRoleIds,
                    ParseEnum<MembershipMode>(g.MembershipMode, $"group '{g.Name}' membershipMode"),
                    g.MembershipScript, g.Email,
                    ParseEnum<EmailMode>(g.EmailMode, $"group '{g.Name}' emailMode"),
                    // Mirror the create endpoint's default (GroupEndpoints: dto.BoundTo ?? [Modgud])
                    // so a manifest group is bound to the IdP and actually confers its roles —
                    // CreateGroupHandler itself defaults null to [] (dormant), which would make an
                    // imported admin group silently grant nothing.
                    g.BoundTo ?? [AppSlugs.Modgud], g.ExternallyDrivable, CallerIsRealmAdmin: true);
                EnsureOk(await groupHandler.Handle(cmd, ct), $"group '{g.Name}'");
            }
        }

        return secrets;
    }

    /// <summary>
    /// In-place upsert of every entity in the manifest against an already-provisioned realm.
    /// Mirrors <see cref="ApplyTenantConfigAsync"/> but reads current state by natural key
    /// and dispatches to the canonical Update op when the entity exists, the Create op when
    /// it doesn't. See <see cref="UpdateRealmAsync"/> for the field-level merge semantics.
    /// </summary>
    private async Task<Dictionary<string, string>> ApplyTenantUpdateAsync(
        string slug, RealmManifest manifest, bool prune, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var apps = new Dictionary<string, App>(StringComparer.Ordinal);        // slug → App (id + catalog)
        var roleIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // role key → id (for groups)
        var userIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // user key → id (for groups)

        using var _ = TenantContext.Enter(slug);
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<IDocumentSession>();

        if (manifest.Settings is not null)
            EnsureOk(await sp.GetRequiredService<IRealmSettingsService>().PatchAsync(manifest.Settings, ct), "settings");

        // ── Apps (+ permission catalog) ───────────────────────────────────────────
        // Seed the resolver with every existing app so downstream entities can reference
        // apps the manifest doesn't re-list, then upsert the manifest's apps over them.
        foreach (var existing in await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct))
            apps[existing.Slug] = existing;

        var appAdmin = sp.GetRequiredService<AppAdminService>();
        foreach (var app in manifest.Apps)
        {
            App result;
            if (apps.TryGetValue(app.Slug, out var current))
            {
                // Preserve existing catalog-entry ids by resource:action so an unchanged
                // permission keeps its id — otherwise it would look "removed + re-added" and
                // trip the catalog-delete block (which guards FK references from roles/RSes).
                // Genuinely new entries carry a null id (minted fresh); genuinely removed ones
                // are then correctly subject to the reference check.
                var byKey = current.Permissions.ToDictionary(p => $"{p.Resource}:{p.Action}", p => p.Id);
                var permissions = app.Permissions.Select(p => new AppPermissionDto(
                    byKey.TryGetValue($"{p.Resource}:{p.Action}", out var existingId)
                        ? new ShortGuid(existingId).ToString()
                        : null,
                    p.Resource, p.Action, p.Description)).ToList();
                var updated = await appAdmin.UpdateAppAsync(current.Id,
                    new UpdateAppDto(app.DisplayName, app.Description, permissions), ct);
                EnsureOk(updated, $"app '{app.Slug}'");
                result = updated.Value;
            }
            else
            {
                var permissions = app.Permissions
                    .Select(p => new AppPermissionDto(null, p.Resource, p.Action, p.Description)).ToList();
                var created = await appAdmin.CreateAppAsync(
                    new CreateAppDto(app.Slug, app.DisplayName, app.Description, permissions), ct);
                EnsureOk(created, $"app '{app.Slug}'");
                result = created.Value;
            }
            apps[app.Slug] = result;
        }

        var oauth = sp.GetRequiredService<OAuthAdminService>();

        // ── OAuth APIs (natural key = Name / aud) ──────────────────────────────────
        foreach (var api in manifest.Apis)
        {
            var ctx = $"api '{api.Name}'";
            var existing = await session.Query<OAuthApiState>()
                .FirstOrDefaultAsync(x => x.Name == api.Name && !x.IsDeleted, ct);
            if (existing is null)
            {
                EnsureOk(await oauth.CreateApiAsync(new CreateOAuthApiDto
                {
                    Name = api.Name,
                    DisplayName = api.DisplayName,
                    Description = api.Description,
                    Enabled = api.Enabled ?? true,
                    Scopes = api.Scopes,
                    UserClaims = api.UserClaims,
                    AppId = ResolveAppId(apps, api.App, ctx),
                    PermissionIds = ResolvePermissionIds(apps, api.App, api.Permissions, ctx),
                    AllowDynamicRegistration = api.AllowDynamicRegistration ?? false,
                }, ct), ctx);
            }
            else
            {
                EnsureOk(await oauth.UpdateApiAsync(existing.Id.ToString(), new UpdateOAuthApiDto
                {
                    DisplayName = api.DisplayName,
                    Description = api.Description,
                    Enabled = api.Enabled,
                    Scopes = NullIfEmpty(api.Scopes),
                    UserClaims = NullIfEmpty(api.UserClaims),
                    AppId = api.App is null ? null : ResolveAppId(apps, api.App, ctx),
                    PermissionIds = NullIfEmpty(ResolvePermissionIds(apps, api.App, api.Permissions, ctx)),
                    AllowDynamicRegistration = api.AllowDynamicRegistration,
                }, ct), ctx);
            }
        }

        // ── OAuth scopes (natural key = Name) ──────────────────────────────────────
        foreach (var s in manifest.Scopes)
        {
            var ctx = $"scope '{s.Name}'";
            var existing = await session.Query<OAuthScopeState>()
                .FirstOrDefaultAsync(x => x.Name == s.Name && !x.IsDeleted, ct);
            if (existing is null)
            {
                EnsureOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
                {
                    Name = s.Name,
                    DisplayName = s.DisplayName,
                    Description = s.Description,
                    Resources = s.Resources,
                    UserClaims = s.UserClaims,
                    Enabled = s.Enabled ?? true,
                    Required = s.Required ?? false,
                    Emphasize = s.Emphasize ?? false,
                    ShowInDiscoveryDocument = s.ShowInDiscoveryDocument ?? true,
                    AppId = ResolveAppId(apps, s.App, ctx),
                }, ct), ctx);
            }
            else
            {
                EnsureOk(await oauth.UpdateScopeAsync(existing.Id.ToString(), new UpdateOAuthScopeDto
                {
                    DisplayName = s.DisplayName,
                    Description = s.Description,
                    Resources = NullIfEmpty(s.Resources),
                    UserClaims = NullIfEmpty(s.UserClaims),
                    Enabled = s.Enabled,
                    Required = s.Required,
                    Emphasize = s.Emphasize,
                    ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
                    AppId = s.App is null ? null : ResolveAppId(apps, s.App, ctx),
                }, ct), ctx);
            }
        }

        // ── OAuth clients (natural key = ClientId) ─────────────────────────────────
        foreach (var c in manifest.Clients)
        {
            var ctx = $"client '{c.ClientId}'";
            var existing = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(x => x.ClientId == c.ClientId && !x.IsDeleted, ct);
            if (existing is null)
            {
                var created = await oauth.CreateClientAsync(new CreateOAuthClientDto
                {
                    ClientId = c.ClientId,
                    DisplayName = c.DisplayName,
                    ClientType = c.ClientType,
                    ClientSecret = c.ClientSecret,
                    RedirectUris = c.RedirectUris,
                    PostLogoutRedirectUris = c.PostLogoutRedirectUris,
                    Scopes = c.Scopes,
                    AllowedGrantTypes = c.AllowedGrantTypes,
                    Roles = c.Roles,
                    WebAuthnRpId = c.WebAuthnRpId,
                    Enabled = c.Enabled ?? true,
                    RequireConsent = c.RequireConsent ?? false,
                    AppIds = c.Apps.Count == 0 ? null
                        : c.Apps.Select(appSlug => ResolveAppId(apps, appSlug, ctx)!).ToList(),
                }, ct);
                EnsureOk(created, ctx);
                if (created.Value.ClientSecret is not null)
                    secrets[c.ClientId] = created.Value.ClientSecret;
            }
            else
            {
                // ClientType + secret are immutable through the canonical update path; an
                // existing client keeps its secret (rotate via the dedicated endpoint).
                EnsureOk(await oauth.UpdateClientAsync(existing.Id.ToString(), new UpdateOAuthClientDto
                {
                    DisplayName = c.DisplayName,
                    RedirectUris = NullIfEmpty(c.RedirectUris),
                    PostLogoutRedirectUris = NullIfEmpty(c.PostLogoutRedirectUris),
                    Scopes = NullIfEmpty(c.Scopes),
                    AllowedGrantTypes = NullIfEmpty(c.AllowedGrantTypes),
                    Roles = NullIfEmpty(c.Roles),
                    WebAuthnRpId = c.WebAuthnRpId,
                    Enabled = c.Enabled,
                    RequireConsent = c.RequireConsent,
                    AppIds = c.Apps.Count == 0 ? null
                        : c.Apps.Select(appSlug => ResolveAppId(apps, appSlug, ctx)!).ToList(),
                }, ct), ctx);
            }
        }

        // ── Roles (natural key = Name) ─────────────────────────────────────────────
        var roleAdmin = sp.GetRequiredService<RoleAdminService>();
        foreach (var r in manifest.Roles)
        {
            var ctx = $"role '{r.Name}'";
            var payload = new RolePayload(
                r.Name,
                r.Description,
                ResolveAppId(apps, r.App, ctx),
                r.IsRealmAdmin,
                ResolvePermissionIds(apps, r.App, r.Permissions, ctx));
            var existing = await session.Query<PermissionRole>()
                .FirstOrDefaultAsync(x => x.Name == r.Name && !x.IsDeleted, ct);
            // Control-plane provisioning is trusted, so the realm-admin guard is satisfied.
            ErrorOr<PermissionRole> result = existing is null
                ? await roleAdmin.CreateRoleAsync(payload, callerIsRealmAdmin: true, ct)
                : await roleAdmin.UpdateRoleAsync(existing.Id, payload, callerIsRealmAdmin: true, ct);
            EnsureOk(result, ctx);
            roleIds[r.ResolveKey()] = result.Value.Id;
        }

        // ── Users (natural key = email or username) ────────────────────────────────
        var setPassword = sp.GetRequiredService<SetUserPasswordHandler>();
        var createUser = new CreateUserHandler(
            session,
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IApplicationSettingsResolver>());
        foreach (var u in manifest.Users)
        {
            var ctx = $"user '{u.Email}'";
            var normalizedEmail = u.Email.ToUpperInvariant();
            var normalizedUserName = u.UserName?.ToLowerInvariant();
            var existing = await session.Query<Person>()
                .FirstOrDefaultAsync(p => !p.IsDeleted &&
                    (p.NormalizedEmail == normalizedEmail ||
                     (normalizedUserName != null && p.AccountName == normalizedUserName)), ct);

            Guid? uid;
            if (existing is null)
            {
                var createCmd = new CreateUserCommand(u.Firstname, u.Lastname, u.Acronym, u.Email,
                    u.UserName ?? string.Empty, u.Password, u.EmailConfirmed);
                var created = await createUser.Handle(createCmd, ct);
                EnsureOk(created, ctx);
                uid = ShortGuid.TryParse(created.Value.Id, out Guid cid) ? cid : null;
            }
            else
            {
                // UpdateUserCommand mutates only the profile fields. Password / EmailConfirmed
                // / active-state are divergent inline ops (Stage 2) — left untouched here.
                var updateCmd = new UpdateUserCommand(existing.Id,
                    OptionalOf(u.Firstname), OptionalOf(u.Lastname), OptionalOf(u.Acronym),
                    new Optional<string>(u.Email), OptionalOf(u.UserName));
                // Direct invocation keeps the manifest update sequential and makes
                // the canonical handler result available for contextual errors.
                var updateHandler = new UpdateUserHandler(session);
                var updated = await updateHandler.Handle(updateCmd,
                    sp.GetRequiredService<IUserAccessRevoker>(),
                    sp.GetRequiredService<IApplicationSettingsResolver>(), ct);
                EnsureOk(updated, ctx);
                uid = existing.Id;

                // A manifest password on an EXISTING user IS applied (the profile update
                // alone never touches the password) — this is what makes the
                // export → edit → "set a password" → apply flow work. New users already
                // get their password at create via CreateUserCommand above.
                if (!string.IsNullOrWhiteSpace(u.Password))
                    EnsureOk(await setPassword.Handle(existing.Id, u.Password, ct), $"{ctx} password");
            }
            if (uid.HasValue) userIds[u.ResolveKey()] = uid.Value;
        }

        // ── Groups (natural key = Name) ───────────────────────────────────────────
        if (manifest.Groups.Count > 0)
        {
            var groupSession = sp.GetRequiredService<IDocumentSession>();
            var evaluator = sp.GetRequiredService<IMembershipEvaluator>();
            var recalculator = sp.GetRequiredService<IAutoMembershipRecalculator>();
            var createHandler = new CreateGroupHandler(groupSession, evaluator, recalculator);
            var updateHandler = new UpdateGroupHandler(groupSession, evaluator,
                sp.GetRequiredService<IPermissionService>(), recalculator);

            foreach (var g in manifest.Groups)
            {
                var ctx = $"group '{g.Name}'";
                // Members/roles may reference entities created this run OR pre-existing ones,
                // so fall back to a DB lookup by key when the in-run map misses.
                var memberIds = new List<Guid>(g.Members.Count);
                foreach (var m in g.Members)
                    memberIds.Add(await ResolveUserRefAsync(session, userIds, m, $"{ctx} member '{m}'", ct));
                var groupRoleIds = new List<Guid>(g.Roles.Count);
                foreach (var rk in g.Roles)
                    groupRoleIds.Add(await ResolveRoleRefAsync(session, roleIds, rk, $"{ctx} role '{rk}'", ct));

                var mode = ParseEnum<MembershipMode>(g.MembershipMode, $"{ctx} membershipMode");
                var emailMode = ParseEnum<EmailMode>(g.EmailMode, $"{ctx} emailMode");

                var existing = await session.Query<Group>()
                    .FirstOrDefaultAsync(x => x.Name == g.Name && !x.IsDeleted, ct);
                if (existing is null)
                {
                    // Create-branch mirrors the create endpoint's BoundTo default (see import).
                    EnsureOk(await createHandler.Handle(new CreateGroupCommand(
                        g.Name, g.Description, memberIds, groupRoleIds, mode,
                        g.MembershipScript, g.Email, emailMode,
                        g.BoundTo ?? [AppSlugs.Modgud], g.ExternallyDrivable, CallerIsRealmAdmin: true), ct), ctx);
                }
                else
                {
                    EnsureOk(await updateHandler.Handle(new UpdateGroupCommand(
                        existing.Id, g.Name, g.Description, memberIds, groupRoleIds, mode,
                        g.MembershipScript, g.Email, emailMode,
                        g.BoundTo, g.ExternallyDrivable, CallerIsRealmAdmin: true), ct), ctx);
                }
            }
        }

        // ── Prune: full-sync removal of entities absent from the manifest. Runs AFTER the
        //    upsert so the protection checks see the realm's desired (post-merge) role graph.
        if (prune)
            await PruneAsync(sp, session, manifest, appAdmin, oauth, roleAdmin, ct);

        return secrets;
    }

    /// <summary>
    /// Deletes every entity that exists in the realm but is absent from the manifest, each via
    /// its canonical delete op (the same the admin API uses), in reverse-dependency order so a
    /// dependent is gone before the app / role it points at — clients → scopes → apis → groups
    /// → users → roles → apps. An app still referenced by a manifest-KEPT role / resource server
    /// correctly errors (surfaced via <see cref="ManifestApplyException"/>).
    ///
    /// <para>NEVER pruned (infrastructure + lockout protection — the robust superset of "System
    /// + last admin": protect ALL admins so no manifest can lock the realm out): the system app
    /// (<c>IsSystem</c>), auto-seeded standard scopes (<c>StandardScopes.IsStandard</c>),
    /// service-account-linked clients (<c>LinkedServiceAccountId</c>), any realm-admin role
    /// (<c>IsRealmAdmin</c>), any user who currently holds <c>realm:admin</c>, and any group that
    /// confers <c>realm:admin</c> (else pruning an admin's group silently strips their admin path
    /// even though the role + user survive).</para>
    ///
    /// <para>Tenant durability (same trap as create/update): user delete runs through
    /// <see cref="DeleteUsersHandler"/> and group delete through <see cref="DeleteGroupHandler"/>
    /// on the PLAIN tenant session, NOT the bus — <c>UserDeactivatedEvent</c> /
    /// <c>GroupDeletedEvent</c> have durable ReferenceSync forwarders that would write
    /// <c>wolverine_*_envelopes</c> a tenant DB lacks. OAuth / app / role deletes go through their
    /// services on the same scoped session.</para>
    /// </summary>
    private async Task PruneAsync(
        IServiceProvider sp, IDocumentSession session, RealmManifest manifest,
        AppAdminService appAdmin, OAuthAdminService oauth, RoleAdminService roleAdmin,
        CancellationToken ct)
    {
        var perms = sp.GetRequiredService<IPermissionService>();

        // ── Clients (natural key = ClientId) — keep SA-linked (auto-managed, not modelled). ──
        var keepClients = manifest.Clients.Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);
        foreach (var c in await session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepClients.Contains(c.ClientId) || c.LinkedServiceAccountId.HasValue) continue;
            EnsureOk(await oauth.DeleteClientAsync(c.Id.ToString(), ct), $"prune client '{c.ClientId}'");
        }

        // ── Scopes (natural key = Name) — keep auto-seeded standard scopes. ──────────────────
        var keepScopes = manifest.Scopes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var s in await session.Query<OAuthScopeState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepScopes.Contains(s.Name) || StandardScopes.IsStandard(s.Name)) continue;
            EnsureOk(await oauth.DeleteScopeAsync(s.Id.ToString(), ct), $"prune scope '{s.Name}'");
        }

        // ── APIs (natural key = Name / aud). ─────────────────────────────────────────────────
        var keepApis = manifest.Apis.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var a in await session.Query<OAuthApiState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepApis.Contains(a.Name)) continue;
            EnsureOk(await oauth.DeleteApiAsync(a.Id.ToString(), ct), $"prune api '{a.Name}'");
        }

        // ── Groups (natural key = Name) — keep admin-conferring groups (lockout guard). ──────
        var keepGroups = manifest.Groups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var groupHandler = new DeleteGroupHandler(session);
        foreach (var g in await session.Query<Group>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepGroups.Contains(g.Name)) continue;
            if (await GroupMembershipGuards.GroupConfersRealmAdminAsync(session, perms, g, ct)) continue;
            EnsureOk(await groupHandler.Handle(new DeleteGroupCommand(g.Id), ct), $"prune group '{g.Name}'");
        }

        // ── Users (natural key = email / username) — keep anyone who holds realm:admin. ──────
        var keepEmails = manifest.Users.Select(u => u.Email.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var keepUserNames = manifest.Users
            .Where(u => !string.IsNullOrEmpty(u.UserName))
            .Select(u => u.UserName!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var userHandler = new DeleteUsersHandler(
            session,
            sp.GetRequiredService<IUserAccessRevoker>(),
            sp.GetRequiredService<IRealmSettingsService>(),
            sp.GetRequiredService<TimeProvider>());
        foreach (var p in await session.Query<Person>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepEmails.Contains(p.NormalizedEmail ?? string.Empty) ||
                (p.AccountName is not null && keepUserNames.Contains(p.AccountName))) continue;
            if (await perms.HasPermissionAsync(p.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct))
                continue;
            EnsureOk(await userHandler.Handle(new DeleteUsersCommand([p.Id]), ct), $"prune user '{p.AccountName ?? p.Id.ToString()}'");
        }

        // ── Roles (natural key = Name) — keep realm-admin roles (lockout guard). ─────────────
        var keepRoles = manifest.Roles.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var r in await session.Query<PermissionRole>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepRoles.Contains(r.Name) || r.IsRealmAdmin) continue;
            EnsureOk(await roleAdmin.DeleteRoleAsync(r.Id, ct), $"prune role '{r.Name}'");
        }

        // ── Apps (natural key = Slug) — keep the system app; a still-referenced app errors. ──
        var keepApps = manifest.Apps.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
        foreach (var a in await session.Query<App>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepApps.Contains(a.Slug) || a.IsSystem) continue;
            EnsureOk(await appAdmin.DeleteAppAsync(a.Id, ct), $"prune app '{a.Slug}'");
        }
    }

    /// <summary>Wraps a manifest string in a "some" optional, or "none" when null — the
    /// UpdateUserCommand semantics: a null manifest field leaves the stored value unchanged
    /// rather than clearing it.</summary>
    private static Optional<string> OptionalOf(string? value)
        => value is null ? Optional<string>.None : new Optional<string>(value);

    /// <summary>Returns null for an empty list so a canonical PATCH op treats it as
    /// "no change" rather than "clear" — UpdateRealm sets and changes lists but never
    /// clears them to empty (that stays an admin-API operation).</summary>
    private static List<string>? NullIfEmpty(List<string> list) => list.Count == 0 ? null : list;

    private static async Task<Guid> ResolveUserRefAsync(
        IDocumentSession session, IReadOnlyDictionary<string, Guid> map, string key, string context, CancellationToken ct)
    {
        if (map.TryGetValue(key, out var id)) return id;
        var lowered = key.ToLowerInvariant();
        var upper = key.ToUpperInvariant();
        var person = await session.Query<Person>()
            .FirstOrDefaultAsync(p => !p.IsDeleted && (p.AccountName == lowered || p.NormalizedEmail == upper), ct);
        if (person is null)
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.UnknownReference", $"{context} resolves to no user.")]);
        return person.Id;
    }

    private static async Task<Guid> ResolveRoleRefAsync(
        IDocumentSession session, IReadOnlyDictionary<string, Guid> map, string key, string context, CancellationToken ct)
    {
        if (map.TryGetValue(key, out var id)) return id;
        var role = await session.Query<PermissionRole>()
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.Name == key, ct);
        if (role is null)
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.UnknownReference", $"{context} resolves to no role.")]);
        return role.Id;
    }

    private static string? ResolveAppId(IReadOnlyDictionary<string, App> apps, string? slug, string context)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        if (!apps.TryGetValue(slug, out var app))
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.UnknownApp", $"{context} references unknown app '{slug}'.")]);
        return new ShortGuid(app.Id).ToString();
    }

    private static List<string> ResolvePermissionIds(
        IReadOnlyDictionary<string, App> apps, string? appSlug, List<RealmManifestPermission> perms, string context)
    {
        if (perms.Count == 0) return [];
        if (string.IsNullOrEmpty(appSlug) || !apps.TryGetValue(appSlug, out var app))
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.PermissionsNeedApp", $"{context} lists permissions but has no resolvable app.")]);

        var catalog = app.Permissions.ToDictionary(p => $"{p.Resource}:{p.Action}", p => p.Id);
        var ids = new List<string>(perms.Count);
        foreach (var p in perms)
        {
            if (!catalog.TryGetValue($"{p.Resource}:{p.Action}", out var pid))
                throw new ManifestApplyException(context,
                    [Error.Validation("Manifest.UnknownPermission",
                        $"{context} references permission '{p.Resource}:{p.Action}' not in app '{appSlug}' catalog.")]);
            ids.Add(new ShortGuid(pid).ToString());
        }
        return ids;
    }

    private static Guid ResolveRef(IReadOnlyDictionary<string, Guid> map, string key, string context)
    {
        if (!map.TryGetValue(key, out var id))
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.UnknownReference", $"{context} references an unknown key.")]);
        return id;
    }

    private static TEnum ParseEnum<TEnum>(string value, string context) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.InvalidEnum", $"'{value}' is not a valid {typeof(TEnum).Name}.")]);
        return result;
    }

    private static void EnsureOk<T>(ErrorOr<T> result, string what)
    {
        if (result.IsError)
            throw new ManifestApplyException(what, result.Errors);
    }

    private sealed class ManifestApplyException(string what, List<Error> errors)
        : Exception($"Failed to apply {what}: {(errors.Count > 0 ? errors[0].Description : "unknown error")}")
    {
        public string What { get; } = what;
        public List<Error> Errors { get; } = errors;
    }
}
