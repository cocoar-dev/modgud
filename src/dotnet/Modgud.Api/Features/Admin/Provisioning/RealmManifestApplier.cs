using System.Text.Json;
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
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.Users;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Saml;
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
using Modgud.Domain.OAuth.Common;
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
public sealed partial class RealmManifestApplier(
    IRealmProvisioningService realms,
    IServiceScopeFactory scopeFactory,
    IDocumentStore store,
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
                "Imported realm {Slug}: {Apps} apps, {Apis} apis, {Scopes} scopes, {Clients} clients, {Roles} roles, {Users} users, {Groups} groups, {Providers} login providers, {Positions} positions.",
                slug, manifest.Apps.Count, manifest.Apis.Count, manifest.Scopes.Count,
                manifest.Clients.Count, manifest.Roles.Count, manifest.Users.Count, manifest.Groups.Count,
                manifest.LoginProviders.Count, manifest.Positions.Count);
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
    /// <para>Atomicity (ADR-0005 Phase 0): the whole update runs inside ONE
    /// <see cref="TenantApplyTransaction"/> on the tenant database — every canonical op's
    /// SaveChanges flushes into that shared transaction without committing it, and a failure
    /// anywhere rolls the entire apply back, leaving the realm untouched. Consequence actions
    /// (token revocation, staffing-session termination — see the <c>Deferring*</c> revoker
    /// decorators) are collected during the apply and executed only after the commit; on
    /// rollback they are discarded. The upserts remain idempotent, so re-applying after a
    /// fixed manifest is still safe.</para>
    ///
    /// <para>When <paramref name="prune"/> is set the merge becomes a full sync (k8s
    /// <c>apply --prune</c>): after the upsert, every entity that exists in the realm but is
    /// absent from the manifest is deleted via its canonical delete op, in reverse-dependency
    /// order. Lockout- and infrastructure-protected entities are NEVER pruned — the system app,
    /// auto-seeded standard scopes, service-account-linked clients, and anything conferring
    /// <c>realm:admin</c> (a realm-admin role, any user who currently holds realm:admin, and any
    /// admin-conferring group). Without the flag the additive merge above is unchanged.</para>
    ///
    /// <para><paramref name="deletions"/> (ADR-0005 staged deletes) are prune's per-entity
    /// counterpart: only the listed (section, key) targets are deleted, through the SAME
    /// canonical delete ops, guards and reverse-dependency order — inside the same apply
    /// transaction. Protection violations throw (the draft plan gate flags them as errors
    /// beforehand).</para>
    /// </summary>
    public async Task<ErrorOr<RealmImportResult>> UpdateRealmAsync(
        RealmManifest manifest, bool prune = false,
        IReadOnlyCollection<RealmDraftDeletion>? deletions = null, CancellationToken ct = default)
    {
        var slug = manifest.Realm.Slug;

        var realm = await realms.GetRealmBySlugAsync(slug, ct);
        if (realm is null)
            return Error.NotFound("Realm.NotFound",
                $"Realm '{slug}' does not exist. Use ImportNewRealm to create it.");

        try
        {
            var secrets = await ApplyTenantUpdateAsync(slug, manifest, prune, deletions, ct);
            logger.LogInformation(
                "Updated realm {Slug}: {Apps} apps, {Apis} apis, {Scopes} scopes, {Clients} clients, {Roles} roles, {Users} users, {Groups} groups, {Providers} login providers, {Positions} positions (in-place merge).",
                slug, manifest.Apps.Count, manifest.Apis.Count, manifest.Scopes.Count,
                manifest.Clients.Count, manifest.Roles.Count, manifest.Users.Count, manifest.Groups.Count,
                manifest.LoginProviders.Count, manifest.Positions.Count);
            return new RealmImportResult
            {
                Slug = slug,
                PrimaryDomain = realm.PrimaryDomain,
                ClientSecrets = secrets,
            };
        }
        catch (ManifestApplyException ex)
        {
            // The apply transaction rolled back — the realm is exactly as it was before the
            // apply, and no deferred consequence ran. Surface the error so the caller can
            // fix the manifest and re-apply.
            logger.LogError(ex,
                "Manifest update failed for realm {Slug} ({What}); the apply transaction was rolled back.",
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
            var dto = new CreateAppDto(app.Slug, app.DisplayName, OrNull(app.Description),
                (app.Permissions ?? []).Select(p => new AppPermissionDto(null, p.Resource, p.Action, p.Description)).ToList(),
                app.Settings, app.Id);
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
                Id = api.Id,
                Name = api.Name,
                DisplayName = OrNull(api.DisplayName),
                Description = OrNull(api.Description),
                Enabled = api.Enabled ?? true,
                Scopes = api.Scopes ?? [],
                UserClaims = api.UserClaims ?? [],
                AppId = ResolveAppId(apps, OrNull(api.App), $"api '{api.Name}'"),
                PermissionIds = ResolvePermissionIds(apps, OrNull(api.App), api.Permissions, $"api '{api.Name}'"),
                AllowDynamicRegistration = api.AllowDynamicRegistration ?? false,
            }, ct), $"api '{api.Name}'");
        }

        // ── OAuth scopes ──────────────────────────────────────────────────────────
        foreach (var s in manifest.Scopes)
        {
            EnsureOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
            {
                Id = s.Id,
                Name = s.Name,
                DisplayName = OrNull(s.DisplayName),
                Description = OrNull(s.Description),
                Resources = s.Resources ?? [],
                UserClaims = s.UserClaims ?? [],
                Enabled = s.Enabled ?? true,
                Required = s.Required ?? false,
                Emphasize = s.Emphasize ?? false,
                ShowInDiscoveryDocument = s.ShowInDiscoveryDocument ?? true,
                AllowDynamicRegistrationClients = s.AllowDynamicRegistrationClients ?? false,
                AppId = ResolveAppId(apps, OrNull(s.App), $"scope '{s.Name}'"),
            }, ct), $"scope '{s.Name}'");
        }

        // ── OAuth clients ─────────────────────────────────────────────────────────
        foreach (var c in manifest.Clients)
        {
            var created = await oauth.CreateClientAsync(
                BuildClientCreateDto(c, apps, $"client '{c.ClientId}'"), ct);
            EnsureOk(created, $"client '{c.ClientId}'");
            if (created.Value.ClientSecret is not null)
                secrets[c.ClientId] = created.Value.ClientSecret;
        }

        // ── Login providers — canonical handler on the manifest's tenant session ──
        foreach (var lp in manifest.LoginProviders)
        {
            var ctx = $"login provider '{lp.Slug}'";
            var pinnedLp = await ResolvePinnedAsync<LoginProvider>(
                sp.GetRequiredService<IDocumentSession>(), lp.Id, "LoginProvider", ctx,
                x => x.IsDeleted, ct);
            EnsureOk(await BuildCreateProviderHandler(sp).Handle(
                BuildCreateProviderCommand(lp, ctx, pinnedLp), ct), ctx);
        }

        // ── Roles (app-scoped or realm-admin) ─────────────────────────────────────
        var roleAdmin = sp.GetRequiredService<RoleAdminService>();
        foreach (var r in manifest.Roles)
        {
            var payload = new RolePayload(
                r.Name,
                OrNull(r.Description),
                ResolveAppId(apps, r.App, $"role '{r.Name}'"),
                r.IsRealmAdmin ?? false,
                ResolvePermissionIds(apps, r.App, r.Permissions, $"role '{r.Name}'"),
                r.Id);
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
            var ctx = $"user '{u.Email}'";
            var cmd = new CreateUserCommand(OrNull(u.Firstname), OrNull(u.Lastname), OrNull(u.Acronym), u.Email,
                u.UserName ?? string.Empty, u.Password, u.EmailConfirmed ?? false,
                Id: await ResolvePinnedUserAsync(userSession, u.Id, ctx, ct));
            var created = await createUser.Handle(cmd, ct);
            EnsureOk(created, ctx);
            if (ShortGuid.TryParse(created.Value.Id, out Guid uid))
                userIds[u.ResolveKey()] = uid;
        }

        // ── Groups — canonical handler on the manifest's tenant session ───────────
        // Keep the same explicit, sequential dispatch used for users so reference
        // resolution and contextual import failures remain deterministic.
        if (manifest.Groups.Count > 0)
        {
            var groupSession = sp.GetRequiredService<IDocumentSession>();
            var groupHandler = new CreateGroupHandler(
                groupSession,
                sp.GetRequiredService<IMembershipEvaluator>(),
                sp.GetRequiredService<IAutoMembershipRecalculator>());

            foreach (var g in manifest.Groups)
            {
                var memberIds = (g.Members ?? []).Select(m => ResolveRef(userIds, m, $"group '{g.Name}' member '{m}'")).ToList();
                var groupRoleIds = (g.Roles ?? []).Select(rk => ResolveRef(roleIds, rk, $"group '{g.Name}' role '{rk}'")).ToList();
                var pinnedGroup = await ResolvePinnedAsync<Group>(
                    groupSession, g.Id, "Group", $"group '{g.Name}'", x => x.IsDeleted, ct);
                var cmd = new CreateGroupCommand(
                    g.Name, OrNull(g.Description), memberIds, groupRoleIds,
                    ParseEnum<MembershipMode>(g.MembershipMode ?? "Manual", $"group '{g.Name}' membershipMode"),
                    g.MembershipScript, OrNull(g.Email),
                    ParseEnum<EmailMode>(g.EmailMode ?? "Shared", $"group '{g.Name}' emailMode"),
                    // Mirror the create endpoint's default (GroupEndpoints: dto.BoundTo ?? [Modgud])
                    // so a manifest group is bound to the IdP and actually confers its roles —
                    // CreateGroupHandler itself defaults null to [] (dormant), which would make an
                    // imported admin group silently grant nothing.
                    g.BoundTo ?? [AppSlugs.Modgud], g.ExternallyDrivable ?? false, CallerIsRealmAdmin: true,
                    Id: pinnedGroup.Id, ReviveExistingStream: pinnedGroup.Revive);
                EnsureOk(await groupHandler.Handle(cmd, ct), $"group '{g.Name}'");
            }
        }

        // ── Service accounts (hulls, id-pinned creates) ───────────────────────────
        await ApplyServiceAccountsAsync(sp, manifest, ct);

        // ── Positions (MG-FT) — after users so grants can resolve their keys ──────
        await ApplyPositionsAsync(sp, manifest, userIds, ct);

        return secrets;
    }

    /// <summary>
    /// In-place upsert of every entity in the manifest against an already-provisioned realm.
    /// Mirrors <see cref="ApplyTenantConfigAsync"/> but reads current state by natural key
    /// and dispatches to the canonical Update op when the entity exists, the Create op when
    /// it doesn't. See <see cref="UpdateRealmAsync"/> for the field-level merge semantics.
    /// </summary>
    private async Task<Dictionary<string, string>> ApplyTenantUpdateAsync(
        string slug, RealmManifest manifest, bool prune,
        IReadOnlyCollection<RealmDraftDeletion>? deletions, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        using var _ = TenantContext.Enter(slug);

        // ADR-0005 Phase 0: one transaction for the whole apply. Activate() installs
        // the ambient marker synchronously so TenantedSessionFactory binds every
        // session below to this transaction and the Deferring* revokers collect
        // their cascades instead of running them. Commit happens after the last
        // section; any ManifestApplyException unwinds through the usings and the
        // DisposeAsync rolls everything back.
        await using var applyTx = await TenantApplyTransaction.BeginAsync(store, slug, ct);
        using (applyTx.Activate())
        {
            await ApplyTenantUpdateSectionsAsync(manifest, prune, deletions, secrets, ct);
            await applyTx.CommitAsync(ct);
        }

        // Consequences (token revocation, staffing-session termination) run only now,
        // in fresh scopes against the committed state; the ambient marker is gone.
        await applyTx.RunDeferredAsync(scopeFactory, logger, ct);

        return secrets;
    }

    private async Task ApplyTenantUpdateSectionsAsync(
        RealmManifest manifest, bool prune, IReadOnlyCollection<RealmDraftDeletion>? deletions,
        Dictionary<string, string> secrets, CancellationToken ct)
    {
        var apps = new Dictionary<string, App>(StringComparer.Ordinal);        // slug → App (id + catalog)
        var roleIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // role key → id (for groups)
        var userIds = new Dictionary<string, Guid>(StringComparer.Ordinal);    // user key → id (for groups)

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
            // Id first: it names the entity outright. An app slug can't be renamed through
            // the canonical update, so an id naming a differently-slugged app is an error.
            var byId = await MatchByPinnedIdAsync<App>(session, app.Id, a => a.IsDeleted, ct);
            if (byId is not null)
                EnsureRenameable(false, app.Slug, byId.Slug, "Slug", $"app '{app.Slug}'");
            var current = byId ?? apps.GetValueOrDefault(app.Slug);
            if (current is not null)
            {
                // v2 merge-patch: an absent catalog keeps the current one verbatim; a
                // present catalog (incl. []) replaces. Preserve existing catalog-entry
                // ids by resource:action so an unchanged permission keeps its id —
                // otherwise it would look "removed + re-added" and trip the
                // catalog-delete block (which guards FK references from roles/RSes).
                var byKey = current.Permissions.ToDictionary(p => $"{p.Resource}:{p.Action}", p => p.Id);
                var permissions = app.Permissions is null
                    ? current.Permissions.Select(p => new AppPermissionDto(
                        new ShortGuid(p.Id).ToString(), p.Resource, p.Action, p.Description)).ToList()
                    : app.Permissions.Select(p => new AppPermissionDto(
                        byKey.TryGetValue($"{p.Resource}:{p.Action}", out var existingId)
                            ? new ShortGuid(existingId).ToString()
                            : null,
                        p.Resource, p.Action, p.Description)).ToList();
                var description = app.Description.HasValue ? app.Description.Value : current.Description;
                var updated = await appAdmin.UpdateAppAsync(current.Id,
                    new UpdateAppDto(app.DisplayName, description, permissions, app.Settings), ct);

                EnsureOk(updated, $"app '{app.Slug}'");
                result = updated.Value;
            }
            else
            {
                var permissions = (app.Permissions ?? [])
                    .Select(p => new AppPermissionDto(null, p.Resource, p.Action, p.Description)).ToList();
                var created = await appAdmin.CreateAppAsync(
                    new CreateAppDto(app.Slug, app.DisplayName, OrNull(app.Description), permissions, app.Settings, app.Id), ct);
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
            var existing = await MatchByPinnedIdAsync<OAuthApiState>(session, api.Id, x => x.IsDeleted, ct)
                ?? await session.Query<OAuthApiState>()
                    .FirstOrDefaultAsync(x => x.Name == api.Name && !x.IsDeleted, ct);
            // The audience IS the API's identity for every token consumer — immutable.
            if (existing is not null) EnsureRenameable(false, api.Name, existing.Name, "Name", ctx);
            if (existing is null)
            {
                EnsureOk(await oauth.CreateApiAsync(new CreateOAuthApiDto
                {
                    Id = api.Id,
                    Name = api.Name,
                    DisplayName = OrNull(api.DisplayName),
                    Description = OrNull(api.Description),
                    Enabled = api.Enabled ?? true,
                    Scopes = api.Scopes ?? [],
                    UserClaims = api.UserClaims ?? [],
                    AppId = ResolveAppId(apps, OrNull(api.App), ctx),
                    PermissionIds = ResolvePermissionIds(apps, OrNull(api.App), api.Permissions, ctx),
                    AllowDynamicRegistration = api.AllowDynamicRegistration ?? false,
                }, ct), ctx);
            }
            else
            {
                // v2 merge-patch: presence passes straight through — absent lists
                // stay null (unchanged), [] clears; Optionals carry clears
                // (an explicit null App detaches the RS).
                EnsureOk(await oauth.UpdateApiAsync(existing.Id.ToString(), new UpdateOAuthApiDto
                {
                    DisplayName = api.DisplayName,
                    Description = api.Description,
                    Enabled = api.Enabled,
                    Scopes = api.Scopes,
                    UserClaims = api.UserClaims,
                    AppId = api.App.HasValue
                        ? new Optional<string?>(ResolveAppId(apps, api.App.Value, ctx))
                        : default,
                    PermissionIds = api.Permissions is null
                        ? null
                        : ResolvePermissionIds(apps, OrNull(api.App), api.Permissions, ctx),
                    AllowDynamicRegistration = api.AllowDynamicRegistration,
                }, ct), ctx);
            }
        }

        // ── OAuth scopes (natural key = Name) ──────────────────────────────────────
        foreach (var s in manifest.Scopes)
        {
            var ctx = $"scope '{s.Name}'";
            var existing = await MatchByPinnedIdAsync<OAuthScopeState>(session, s.Id, x => x.IsDeleted, ct)
                ?? await session.Query<OAuthScopeState>()
                    .FirstOrDefaultAsync(x => x.Name == s.Name && !x.IsDeleted, ct);
            if (existing is not null) EnsureRenameable(false, s.Name, existing.Name, "Name", ctx);
            if (existing is null)
            {
                EnsureOk(await oauth.CreateScopeAsync(new CreateOAuthScopeDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    DisplayName = OrNull(s.DisplayName),
                    Description = OrNull(s.Description),
                    Resources = s.Resources ?? [],
                    UserClaims = s.UserClaims ?? [],
                    Enabled = s.Enabled ?? true,
                    Required = s.Required ?? false,
                    Emphasize = s.Emphasize ?? false,
                    ShowInDiscoveryDocument = s.ShowInDiscoveryDocument ?? true,
                    AllowDynamicRegistrationClients = s.AllowDynamicRegistrationClients ?? false,
                    AppId = ResolveAppId(apps, OrNull(s.App), ctx),
                }, ct), ctx);
            }
            else
            {
                // v2 merge-patch: presence passes straight through (an explicit
                // null App detaches the scope back to realm-wide).
                EnsureOk(await oauth.UpdateScopeAsync(existing.Id.ToString(), new UpdateOAuthScopeDto
                {
                    DisplayName = s.DisplayName,
                    Description = s.Description,
                    Resources = s.Resources,
                    UserClaims = s.UserClaims,
                    Enabled = s.Enabled,
                    Required = s.Required,
                    Emphasize = s.Emphasize,
                    ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
                    AllowDynamicRegistrationClients = s.AllowDynamicRegistrationClients,
                    AppId = s.App.HasValue
                        ? new Optional<string?>(ResolveAppId(apps, s.App.Value, ctx))
                        : default,
                }, ct), ctx);
            }
        }

        // ── OAuth clients (natural key = ClientId) ─────────────────────────────────
        foreach (var c in manifest.Clients)
        {
            var ctx = $"client '{c.ClientId}'";
            var existing = await MatchByPinnedIdAsync<OAuthApplicationState>(session, c.Id, x => x.IsDeleted, ct)
                ?? await session.Query<OAuthApplicationState>()
                    .FirstOrDefaultAsync(x => x.ClientId == c.ClientId && !x.IsDeleted, ct);
            if (existing is not null) EnsureRenameable(false, c.ClientId, existing.ClientId, "ClientId", ctx);
            if (existing is null)
            {
                var created = await oauth.CreateClientAsync(BuildClientCreateDto(c, apps, ctx), ct);
                EnsureOk(created, ctx);
                if (created.Value.ClientSecret is not null)
                    secrets[c.ClientId] = created.Value.ClientSecret;
            }
            else
            {
                // ClientType + secret are immutable through the canonical update path; an
                // existing client keeps its secret (rotate via the dedicated endpoint).
                EnsureOk(await oauth.UpdateClientAsync(
                    existing.Id.ToString(), BuildClientUpdateDto(c, apps, ctx), ct), ctx);
            }
        }

        // ── Login providers (natural key = Slug) ───────────────────────────────────
        foreach (var lp in manifest.LoginProviders)
        {
            var ctx = $"login provider '{lp.Slug}'";
            var existing = await MatchByPinnedIdAsync<LoginProvider>(session, lp.Id, x => x.IsDeleted, ct)
                ?? await session.Query<LoginProvider>()
                    .FirstOrDefaultAsync(x => x.Slug == lp.Slug && !x.IsDeleted, ct);
            // The slug owns the provider's callback URLs — immutable after create.
            if (existing is not null) EnsureRenameable(false, lp.Slug, existing.Slug, "Slug", ctx);
            if (existing is null)
            {
                EnsureOk(await BuildCreateProviderHandler(sp).Handle(
                    BuildCreateProviderCommand(lp, ctx,
                        await ResolvePinnedAsync<LoginProvider>(
                            session, lp.Id, "LoginProvider", ctx, x => x.IsDeleted, ct)), ct), ctx);
                continue;
            }

            if (existing.IsBuiltIn)
                throw new ManifestApplyException(ctx, [Error.Validation("Manifest.InternalProviderReserved",
                    $"{ctx} is the seeded built-in provider — it cannot be managed through a manifest.")]);

            // Type + Flavor are immutable after create (they own the provider's URL and
            // config shape) — a differing manifest value is a contract error, not a merge.
            var manifestType = lp.Type is null
                ? existing.Type
                : ParseEnum<LoginProviderType>(lp.Type, $"{ctx} type");
            if (manifestType != existing.Type)
                throw new ManifestApplyException(ctx, [Error.Validation("Manifest.ImmutableField",
                    $"{ctx}: Type is immutable (stored '{existing.Type}', manifest '{lp.Type}'). Delete and recreate the provider to change it.")]);
            if (!string.Equals(lp.Flavor, existing.Flavor, StringComparison.OrdinalIgnoreCase))
                throw new ManifestApplyException(ctx, [Error.Validation("Manifest.ImmutableField",
                    $"{ctx}: Flavor is immutable (stored '{existing.Flavor}', manifest '{lp.Flavor}'). Delete and recreate the provider to change it.")]);

            var updateProvider = new UpdateLoginProviderHandler(
                session,
                sp.GetRequiredService<LoginProviderFlavorRegistry>(),
                sp.GetRequiredService<SamlFlavorRegistry>(),
                sp.GetRequiredService<TimeProvider>());
            // v2 merge-patch: the manifest's Optionals map 1:1 onto the command's —
            // absent stays None, an explicit null carries the clear through.
            EnsureOk(await updateProvider.Handle(new UpdateLoginProviderCommand(
                Id: existing.Id,
                DisplayName: new Optional<string>(lp.DisplayName),
                Description: lp.Description,
                ClientId: lp.ClientId is null ? default : new Optional<string>(lp.ClientId),
                Scopes: lp.Scopes is null ? default : new Optional<List<string>>(lp.Scopes),
                UserUpdateScript: lp.UserUpdateScript is null ? default : new Optional<string>(lp.UserUpdateScript),
                StoreRawClaims: OptBool(lp.StoreRawClaims),
                RawClaimsRetentionDays: lp.RawClaimsRetentionDays,
                AutoCreateUsers: OptBool(lp.AutoCreateUsers),
                AllowLinking: OptBool(lp.AllowLinking),
                TrustForEmailLink: OptBool(lp.TrustForEmailLink),
                AllowedEmailDomains: lp.AllowedEmailDomains.HasValue
                    ? new Optional<List<string>?>(
                        lp.AllowedEmailDomains.Value is { Count: > 0 } domains ? domains : null)
                    : default,
                IconName: lp.IconName,
                ButtonColorHex: lp.ButtonColorHex,
                FlavorData: lp.FlavorData.HasValue
                    ? new Optional<JsonDocument>(JsonDocument.Parse(lp.FlavorData.Value.GetRawText())) : default,
                Enabled: OptBool(lp.Enabled),
                TrustForAuthorization: OptBool(lp.TrustForAuthorization),
                AuthoritativeForProfile: OptBool(lp.AuthoritativeForProfile)), ct), ctx);

            // A manifest secret on an EXISTING provider ROTATES it (mirrors the user
            // Password semantics — this is what makes export → edit → "set the secret"
            // → apply work). New providers store it at create via InitialClientSecret.
            if (!string.IsNullOrWhiteSpace(lp.ClientSecret))
            {
                var rotate = new RotateLoginProviderSecretHandler(
                    session, sp.GetRequiredService<LoginProviderSecretStore>(),
                    sp.GetRequiredService<TimeProvider>());
                EnsureOk(await rotate.Handle(new RotateLoginProviderSecretCommand(
                    existing.Id, lp.ClientSecret, RotatedByUserId: null), ct), $"{ctx} secret");
            }
        }

        // ── Roles (natural key = Name) ─────────────────────────────────────────────
        var roleAdmin = sp.GetRequiredService<RoleAdminService>();
        foreach (var r in manifest.Roles)
        {
            var ctx = $"role '{r.Name}'";
            // Id first — a role's name is mutable, so an id-matched entry RENAMES it.
            var existing = await MatchByPinnedIdAsync<PermissionRole>(session, r.Id, x => x.IsDeleted, ct)
                ?? await session.Query<PermissionRole>()
                    .FirstOrDefaultAsync(x => x.Name == r.Name && !x.IsDeleted, ct);
            // v2 merge-patch: absent fields keep the existing role's values (the
            // canonical update is a full payload replace, so merge here).
            var isRealmAdmin = r.IsRealmAdmin ?? existing?.IsRealmAdmin ?? false;
            var payload = new RolePayload(
                r.Name,
                r.Description.HasValue ? r.Description.Value : existing?.Description,
                r.App is null
                    ? isRealmAdmin || existing?.AppId is not { } appId ? null : new ShortGuid(appId).ToString()
                    : ResolveAppId(apps, r.App, ctx),
                isRealmAdmin,
                r.Permissions is null && existing is not null
                    ? existing.PermissionIds.Select(pid => new ShortGuid(pid).ToString()).ToList()
                    : ResolvePermissionIds(apps, r.App, r.Permissions ?? [], ctx),
                r.Id);
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
            // Id first — the person's email/username are mutable profile fields, so an
            // id-matched entry updates (and renames) that person.
            var existing = await MatchByPinnedIdAsync<Person>(session, u.Id, x => x.IsDeleted, ct)
                ?? await session.Query<Person>()
                    .FirstOrDefaultAsync(p => !p.IsDeleted &&
                        (p.NormalizedEmail == normalizedEmail ||
                         (normalizedUserName != null && p.AccountName == normalizedUserName)), ct);

            Guid? uid;
            if (existing is null)
            {
                var createCmd = new CreateUserCommand(OrNull(u.Firstname), OrNull(u.Lastname), OrNull(u.Acronym),
                    u.Email, u.UserName ?? string.Empty, u.Password, u.EmailConfirmed ?? false,
                    Id: await ResolvePinnedUserAsync(session, u.Id, ctx, ct));
                var created = await createUser.Handle(createCmd, ct);
                EnsureOk(created, ctx);
                uid = ShortGuid.TryParse(created.Value.Id, out Guid cid) ? cid : null;
            }
            else
            {
                // UpdateUserCommand mutates only the profile fields. Password / EmailConfirmed
                // / active-state are divergent inline ops (Stage 2) — left untouched here.
                // v2 merge-patch: an explicit manifest null clears the profile field.
                var updateCmd = new UpdateUserCommand(existing.Id,
                    OptThrough(u.Firstname), OptThrough(u.Lastname), OptThrough(u.Acronym),
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
                // Id first — a group's name is mutable, so an id-matched entry RENAMES it
                // (this is what makes a rename survive an export → import round trip).
                var existing = await MatchByPinnedIdAsync<Group>(session, g.Id, x => x.IsDeleted, ct)
                    ?? await session.Query<Group>()
                        .FirstOrDefaultAsync(x => x.Name == g.Name && !x.IsDeleted, ct);

                // v2 merge-patch: absent lists/fields keep the existing group's values
                // (the canonical update is a full payload replace, so merge here).
                // Members/roles may reference entities created this run OR pre-existing
                // ones, so fall back to a DB lookup by key when the in-run map misses.
                List<Guid> memberIds;
                if (g.Members is null && existing is not null)
                {
                    memberIds = existing.MemberIds.ToList();
                }
                else
                {
                    memberIds = new List<Guid>((g.Members ?? []).Count);
                    foreach (var m in g.Members ?? [])
                        memberIds.Add(await ResolveUserRefAsync(session, userIds, m, $"{ctx} member '{m}'", ct));
                }
                List<Guid> groupRoleIds;
                if (g.Roles is null && existing is not null)
                {
                    groupRoleIds = existing.RoleIds.ToList();
                }
                else
                {
                    groupRoleIds = new List<Guid>((g.Roles ?? []).Count);
                    foreach (var rk in g.Roles ?? [])
                        groupRoleIds.Add(await ResolveRoleRefAsync(session, roleIds, rk, $"{ctx} role '{rk}'", ct));
                }

                var mode = g.MembershipMode is null
                    ? existing?.MembershipMode ?? MembershipMode.Manual
                    : ParseEnum<MembershipMode>(g.MembershipMode, $"{ctx} membershipMode");
                var emailMode = g.EmailMode is null
                    ? existing?.EmailMode ?? EmailMode.Shared
                    : ParseEnum<EmailMode>(g.EmailMode, $"{ctx} emailMode");
                var description = g.Description.HasValue ? g.Description.Value : existing?.Description;
                var email = g.Email.HasValue ? g.Email.Value : existing?.Email;
                var script = g.MembershipScript ?? existing?.MembershipScript;
                var externallyDrivable = g.ExternallyDrivable ?? existing?.ExternallyDrivable ?? false;

                if (existing is null)
                {
                    var pinnedGroup = await ResolvePinnedAsync<Group>(
                        session, g.Id, "Group", ctx, x => x.IsDeleted, ct);
                    // Create-branch mirrors the create endpoint's BoundTo default (see import).
                    EnsureOk(await createHandler.Handle(new CreateGroupCommand(
                        g.Name, description, memberIds, groupRoleIds, mode,
                        script, email, emailMode,
                        g.BoundTo ?? [AppSlugs.Modgud], externallyDrivable, CallerIsRealmAdmin: true,
                        Id: pinnedGroup.Id, ReviveExistingStream: pinnedGroup.Revive), ct), ctx);
                }
                else
                {
                    EnsureOk(await updateHandler.Handle(new UpdateGroupCommand(
                        existing.Id, g.Name, description, memberIds, groupRoleIds, mode,
                        script, email, emailMode,
                        g.BoundTo, externallyDrivable, CallerIsRealmAdmin: true), ct), ctx);
                }
            }
        }

        // ── Service accounts (hulls, id-pinned creates) ───────────────────────────
        await ApplyServiceAccountsAsync(sp, manifest, ct);

        // ── Positions (MG-FT) — after users so grants can resolve their keys ──────
        await ApplyPositionsAsync(sp, manifest, userIds, ct);

        // ── Prune / staged deletions: removal of entities absent from the manifest. Runs
        //    AFTER the upsert so the protection checks see the desired (post-merge) role
        //    graph. Prune sweeps everything; staged deletions target only their keys.
        if (prune || deletions is { Count: > 0 })
            await PruneAsync(sp, session, manifest, appAdmin, oauth, roleAdmin, prune,
                DeletionTargets(deletions), ct);
    }

    /// <summary>Per-section key sets for targeted (staged) deletions; the positions
    /// section normalizes to the lowercased account name (its natural key).</summary>
    private static IReadOnlyDictionary<string, HashSet<string>>? DeletionTargets(
        IReadOnlyCollection<RealmDraftDeletion>? deletions)
        => deletions is not { Count: > 0 }
            ? null
            : deletions
                .GroupBy(d => d.Section, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(d => g.Key == "positions" ? d.Key.Trim().ToLowerInvariant() : d.Key)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

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
        bool prune, IReadOnlyDictionary<string, HashSet<string>>? targeted,
        CancellationToken ct)
    {
        var perms = sp.GetRequiredService<IPermissionService>();

        // Targeted (staged) deletions restrict the sweep to their keys; a full prune
        // deletes every candidate. Everything else — keep-sets, infra/lockout guards,
        // canonical delete ops, ordering — is byte-identical for both modes.
        bool Wants(string section, string key)
            => prune || (targeted?.TryGetValue(section, out var keys) == true && keys.Contains(key));

        // ── Positions (natural key = AccountName) — first: pruning a position cascades its
        //    terminal slots + their terminal-managed clients (see the Positions partial). ─────
        await PrunePositionsAsync(sp, session, oauth, manifest, prune, targeted, ct);

        // ── Clients (natural key = ClientId) — keep SA-linked and terminal-managed clients
        //    (both are auto-managed credential material the manifest doesn't model). ─────────
        var keepClients = manifest.Clients.Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);
        foreach (var c in await session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepClients.Contains(c.ClientId)
                || c.LinkedServiceAccountId.HasValue
                || c.LinkedPositionPrincipalId.HasValue
                || !Wants("clients", c.ClientId)) continue;
            EnsureOk(await oauth.DeleteClientAsync(c.Id.ToString(), ct), $"prune client '{c.ClientId}'");
        }

        // ── Login providers (natural key = Slug) — keep the built-in Internal provider. ──────
        var keepProviders = manifest.LoginProviders.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        var deleteProvider = new DeleteLoginProviderHandler(session, sp.GetRequiredService<TimeProvider>());
        foreach (var p in await session.Query<LoginProvider>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepProviders.Contains(p.Slug) || p.IsBuiltIn || !Wants("loginProviders", p.Slug)) continue;
            EnsureOk(await deleteProvider.Handle(new DeleteLoginProviderCommand(p.Id), ct),
                $"prune login provider '{p.Slug}'");
        }

        // ── Scopes (natural key = Name) — keep auto-seeded standard scopes. ──────────────────
        var keepScopes = manifest.Scopes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var s in await session.Query<OAuthScopeState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepScopes.Contains(s.Name) || StandardScopes.IsStandard(s.Name)
                || !Wants("scopes", s.Name)) continue;
            EnsureOk(await oauth.DeleteScopeAsync(s.Id.ToString(), ct), $"prune scope '{s.Name}'");
        }

        // ── APIs (natural key = Name / aud). ─────────────────────────────────────────────────
        var keepApis = manifest.Apis.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var a in await session.Query<OAuthApiState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepApis.Contains(a.Name) || !Wants("apis", a.Name)) continue;
            EnsureOk(await oauth.DeleteApiAsync(a.Id.ToString(), ct), $"prune api '{a.Name}'");
        }

        // ── Groups (natural key = Name) — keep admin-conferring groups (lockout guard). ──────
        var keepGroups = manifest.Groups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var groupHandler = new DeleteGroupHandler(session);
        foreach (var g in await session.Query<Group>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepGroups.Contains(g.Name) || !Wants("groups", g.Name)) continue;
            if (await GroupMembershipGuards.GroupConfersRealmAdminAsync(session, perms, g, ct)) continue;
            EnsureOk(await groupHandler.Handle(new DeleteGroupCommand(g.Id), ct), $"prune group '{g.Name}'");
        }

        // ── Users (natural key = email / username) — keep anyone who holds realm:admin. ──────
        var keepEmails = manifest.Users.Select(u => u.Email.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var keepUserNames = manifest.Users
            .Where(u => !string.IsNullOrEmpty(u.UserName))
            .Select(u => u.UserName!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        // A staged user deletion carries whatever key the list row showed (username
        // or email) — match either, case-insensitively.
        var targetedUsers = targeted?.GetValueOrDefault("users");
        bool WantsUser(Person p)
            => prune || (targetedUsers is not null && targetedUsers.Any(k =>
                string.Equals(k, p.AccountName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k.ToUpperInvariant(), p.NormalizedEmail, StringComparison.Ordinal)));
        var userHandler = new DeleteUsersHandler(
            session,
            sp.GetRequiredService<IUserAccessRevoker>(),
            sp.GetRequiredService<Modgud.Infrastructure.PositionTerminals.IStaffingRevoker>(),
            sp.GetRequiredService<IRealmSettingsService>(),
            sp.GetRequiredService<TimeProvider>());
        foreach (var p in await session.Query<Person>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepEmails.Contains(p.NormalizedEmail ?? string.Empty) ||
                (p.AccountName is not null && keepUserNames.Contains(p.AccountName)) ||
                !WantsUser(p)) continue;
            if (await perms.HasPermissionAsync(p.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct))
                continue;
            EnsureOk(await userHandler.Handle(new DeleteUsersCommand([p.Id]), ct), $"prune user '{p.AccountName ?? p.Id.ToString()}'");
        }

        // ── Roles (natural key = Name) — keep realm-admin roles (lockout guard). ─────────────
        var keepRoles = manifest.Roles.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var r in await session.Query<PermissionRole>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepRoles.Contains(r.Name) || r.IsRealmAdmin || !Wants("roles", r.Name)) continue;
            EnsureOk(await roleAdmin.DeleteRoleAsync(r.Id, ct), $"prune role '{r.Name}'");
        }

        // ── Apps (natural key = Slug) — keep the system app; a still-referenced app errors. ──
        var keepApps = manifest.Apps.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
        foreach (var a in await session.Query<App>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (keepApps.Contains(a.Slug) || a.IsSystem || !Wants("apps", a.Slug)) continue;
            EnsureOk(await appAdmin.DeleteAppAsync(a.Id, ct), $"prune app '{a.Slug}'");
        }
    }

    /// <summary>
    /// Manifest client → canonical create DTO. Every optional field falls back to the SAME
    /// shipped default the admin API applies on create, so the manifest path and the manual
    /// path can never diverge (guarded by RealmManifestParityTests).
    /// </summary>
    private static CreateOAuthClientDto BuildClientCreateDto(
        RealmManifestClient c, IReadOnlyDictionary<string, App> apps, string ctx) => new()
    {
        Id = c.Id,
        ClientId = c.ClientId,
        DisplayName = OrNull(c.DisplayName),
        ClientType = c.ClientType,
        ClientSecret = c.ClientSecret,
        ConsentType = c.ConsentType ?? "implicit",
        RedirectUris = c.RedirectUris ?? [],
        PostLogoutRedirectUris = c.PostLogoutRedirectUris ?? [],
        Scopes = c.Scopes ?? [],
        AllowedGrantTypes = c.AllowedGrantTypes ?? [],
        Capabilities = c.Capabilities ?? [],
        AllowedCorsOrigins = c.AllowedCorsOrigins ?? [],
        Roles = c.Roles ?? [],
        WebAuthnRpId = OrNull(c.WebAuthnRpId),
        BackChannelLogoutUri = OrNull(c.BackChannelLogoutUri),
        BackChannelLogoutSessionRequired = c.BackChannelLogoutSessionRequired ?? true,
        Enabled = c.Enabled ?? true,
        RequireConsent = c.RequireConsent ?? false,
        AllowRememberConsent = c.AllowRememberConsent ?? true,
        AllowAccessTokensViaBrowser = c.AllowAccessTokensViaBrowser ?? false,
        RequireClientSecret = c.RequireClientSecret ?? true,
        EnableLocalLogin = c.EnableLocalLogin ?? true,
        RequirePushedAuthorizationRequests = c.RequirePushedAuthorizationRequests ?? false,
        RequireDpop = c.RequireDpop ?? false,
        RequireDpopNonce = c.RequireDpopNonce ?? false,
        AccessTokenType = ParseOptionalEnum<AccessTokenType>(c.AccessTokenType, $"{ctx} accessTokenType")
            ?? AccessTokenType.Reference,
        IdentityTokenLifetime = OrNull(c.IdentityTokenLifetime),
        AccessTokenLifetime = OrNull(c.AccessTokenLifetime),
        AuthorizationCodeLifetime = OrNull(c.AuthorizationCodeLifetime),
        SlidingRefreshTokenLifetime = OrNull(c.SlidingRefreshTokenLifetime),
        ClientSessionIdleLifetime = OrNull(c.ClientSessionIdleLifetime),
        ClientSessionAbsoluteLifetime = OrNull(c.ClientSessionAbsoluteLifetime),
        Claims = (c.Claims ?? []).Select(cl => new OAuthClientClaimDto { Type = cl.Type, Value = cl.Value }).ToList(),
        ClientClaimsPrefix = OrNull(c.ClientClaimsPrefix),
        AlwaysSendClientClaims = c.AlwaysSendClientClaims ?? false,
        UpdateAccessTokenClaimsOnRefresh = c.UpdateAccessTokenClaimsOnRefresh ?? false,
        AppIds = c.Apps is not { Count: > 0 }
            ? null
            : c.Apps.Select(appSlug => ResolveAppId(apps, appSlug, ctx)!).ToList(),
    };

    /// <summary>
    /// Manifest client → canonical update DTO. v2 merge-patch is a straight
    /// pass-through: the update DTO shares the manifest's semantics — absent
    /// Optionals/lists stay unchanged, explicit null clears, [] clears a list.
    /// </summary>
    private static UpdateOAuthClientDto BuildClientUpdateDto(
        RealmManifestClient c, IReadOnlyDictionary<string, App> apps, string ctx) => new()
    {
        DisplayName = c.DisplayName,
        ConsentType = c.ConsentType,
        RedirectUris = c.RedirectUris,
        PostLogoutRedirectUris = c.PostLogoutRedirectUris,
        Scopes = c.Scopes,
        AllowedGrantTypes = c.AllowedGrantTypes,
        Capabilities = c.Capabilities,
        AllowedCorsOrigins = c.AllowedCorsOrigins,
        Roles = c.Roles,
        WebAuthnRpId = c.WebAuthnRpId,
        BackChannelLogoutUri = c.BackChannelLogoutUri,
        BackChannelLogoutSessionRequired = c.BackChannelLogoutSessionRequired,
        Enabled = c.Enabled,
        RequireConsent = c.RequireConsent,
        AllowRememberConsent = c.AllowRememberConsent,
        AllowAccessTokensViaBrowser = c.AllowAccessTokensViaBrowser,
        RequireClientSecret = c.RequireClientSecret,
        EnableLocalLogin = c.EnableLocalLogin,
        RequirePushedAuthorizationRequests = c.RequirePushedAuthorizationRequests,
        RequireDpop = c.RequireDpop,
        RequireDpopNonce = c.RequireDpopNonce,
        AccessTokenType = ParseOptionalEnum<AccessTokenType>(c.AccessTokenType, $"{ctx} accessTokenType"),
        IdentityTokenLifetime = c.IdentityTokenLifetime,
        AccessTokenLifetime = c.AccessTokenLifetime,
        AuthorizationCodeLifetime = c.AuthorizationCodeLifetime,
        SlidingRefreshTokenLifetime = c.SlidingRefreshTokenLifetime,
        ClientSessionIdleLifetime = c.ClientSessionIdleLifetime,
        ClientSessionAbsoluteLifetime = c.ClientSessionAbsoluteLifetime,
        Claims = c.Claims?.Select(cl => new OAuthClientClaimDto { Type = cl.Type, Value = cl.Value }).ToList(),
        ClientClaimsPrefix = c.ClientClaimsPrefix,
        AlwaysSendClientClaims = c.AlwaysSendClientClaims,
        UpdateAccessTokenClaimsOnRefresh = c.UpdateAccessTokenClaimsOnRefresh,
        AppIds = c.Apps?.Select(appSlug => ResolveAppId(apps, appSlug, ctx)!).ToList(),
    };

    /// <summary>
    /// The canonical create handler for login providers, on the manifest's tenant session.
    /// Same direct-invocation pattern as users/groups: sequential dispatch with the handler
    /// result surfaced immediately for contextual import errors.
    /// </summary>
    private static CreateLoginProviderHandler BuildCreateProviderHandler(IServiceProvider sp) => new(
        sp.GetRequiredService<IDocumentSession>(),
        sp.GetRequiredService<LoginProviderFlavorRegistry>(),
        sp.GetRequiredService<SamlFlavorRegistry>(),
        sp.GetRequiredService<LoginProviderSecretStore>(),
        sp.GetRequiredService<TimeProvider>());

    /// <summary>
    /// Manifest login provider → canonical create command. The seeded Internal provider is
    /// reserved infrastructure (like the system app / standard scopes) — a manifest that
    /// declares one is a contract error.
    /// </summary>
    private static CreateLoginProviderCommand BuildCreateProviderCommand(
        RealmManifestLoginProvider lp, string ctx, PinnedEntityId.PinnedIdResolution pinned)
    {
        var type = lp.Type is null
            ? LoginProviderType.Oidc
            : ParseEnum<LoginProviderType>(lp.Type, $"{ctx} type");
        if (type == LoginProviderType.Internal)
            throw new ManifestApplyException(ctx, [Error.Validation("Manifest.InternalProviderReserved",
                $"{ctx}: the Internal provider is seeded automatically and cannot be declared in a manifest.")]);

        return new CreateLoginProviderCommand(
            Flavor: lp.Flavor,
            DisplayName: lp.DisplayName,
            Slug: lp.Slug,
            FlavorData: lp.FlavorData.HasValue ? JsonDocument.Parse(lp.FlavorData.Value.GetRawText()) : null,
            Type: type,
            Description: OrNull(lp.Description),
            Enabled: lp.Enabled,
            ClientId: lp.ClientId,
            Scopes: lp.Scopes,
            UserUpdateScript: lp.UserUpdateScript,
            StoreRawClaims: lp.StoreRawClaims,
            RawClaimsRetentionDays: OrNull(lp.RawClaimsRetentionDays),
            AutoCreateUsers: lp.AutoCreateUsers,
            AllowLinking: lp.AllowLinking,
            TrustForEmailLink: lp.TrustForEmailLink,
            AllowedEmailDomains: lp.AllowedEmailDomains.HasValue && lp.AllowedEmailDomains.Value is { Count: > 0 } domains
                ? domains : null,
            IconName: OrNull(lp.IconName),
            ButtonColorHex: OrNull(lp.ButtonColorHex),
            TrustForAuthorization: lp.TrustForAuthorization,
            AuthoritativeForProfile: lp.AuthoritativeForProfile,
            InitialClientSecret: lp.ClientSecret,
            Id: pinned.Id,
            ReviveExistingStream: pinned.Revive);
    }

    /// <summary>Nullable bool → PATCH optional: null = omitted (no change).</summary>
    private static Optional<bool> OptBool(bool? value)
        => value is { } b ? new Optional<bool>(b) : default;

    /// <summary>Wraps a manifest string in a "some" optional, or "none" when null — the
    /// UpdateUserCommand semantics: a null manifest field leaves the stored value unchanged
    /// rather than clearing it.</summary>
    private static Optional<string> OptionalOf(string? value)
        => value is null ? Optional<string>.None : new Optional<string>(value);

    /// <summary>Create-path unwrap: an absent Optional takes the shipped default (null).</summary>
    private static string? OrNull(Optional<string?> value) => value.HasValue ? value.Value : null;

    /// <summary>Create-path unwrap: an absent Optional takes the shipped default (null).</summary>
    private static int? OrNull(Optional<int?> value) => value.HasValue ? value.Value : null;

    /// <summary>Pass-through into a non-nullable <see cref="Optional{T}"/> command param:
    /// absent stays None; explicit null carries the clear (Some(null)) — callers whose
    /// command treats Some(null) differently must not use this for required fields.</summary>
    private static Optional<string> OptThrough(Optional<string?> value)
        => value.HasValue ? new Optional<string>(value.Value!) : default;

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
        IReadOnlyDictionary<string, App> apps, string? appSlug, List<RealmManifestPermission>? perms, string context)
    {
        if (perms is not { Count: > 0 }) return [];
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

    /// <summary>Null stays null — the manifest's "omitted = no change on apply / shipped
    /// default on create" semantics for optional enum fields.</summary>
    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string context) where TEnum : struct, Enum
        => value is null ? null : ParseEnum<TEnum>(value, context);

    private static TEnum ParseEnum<TEnum>(string value, string context) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
            throw new ManifestApplyException(context,
                [Error.Validation("Manifest.InvalidEnum", $"'{value}' is not a valid {typeof(TEnum).Name}.")]);
        return result;
    }

    /// <summary>Pinned-id resolution for the COMMAND-based create paths (users, groups,
    /// login providers, positions): the handlers live in projects that cannot see
    /// <see cref="PinnedEntityId"/>, so the applier resolves the id here and passes the
    /// outcome down. Service-backed creates (apps, apis, scopes, clients, roles, service
    /// accounts) resolve inside the service instead. On update the manifest id is ignored —
    /// ids are immutable.
    ///
    /// <para>A pinned id whose stream holds a SOFT-DELETED document of the same type is a
    /// REVIVE (the create appends onto that stream); a live entity — or a stream of another
    /// type — is a conflict that fails the apply with the section context.</para></summary>
    private static async Task<PinnedEntityId.PinnedIdResolution> ResolvePinnedAsync<TDoc>(
        IDocumentSession session, string? raw, string entityLabel, string ctx,
        Func<TDoc, bool> isDeleted, CancellationToken ct)
        where TDoc : class
    {
        var pinned = await PinnedEntityId.ResolveAsync(session, raw, entityLabel, isDeleted, ct);
        if (pinned.IsError) throw new ManifestApplyException(ctx, pinned.Errors);
        return pinned.Value;
    }

    /// <summary>
    /// Id-first entity matching: a manifest entity carrying an <c>Id</c> that resolves to a
    /// LIVE document of its own type IS that entity — an import updates it to the manifest's
    /// values, INCLUDING its natural key (the id is the identity; the key is mutable
    /// metadata). Returns null when the entity carries no id, or when the id is free / owned
    /// by a soft-deleted entity (then the caller falls back to the natural key, and a create
    /// pins or revives the id).
    /// </summary>
    private static async Task<TDoc?> MatchByPinnedIdAsync<TDoc>(
        IDocumentSession session, string? raw, Func<TDoc, bool> isDeleted, CancellationToken ct)
        where TDoc : class
    {
        if (string.IsNullOrWhiteSpace(raw) || !ShortGuid.TryParse(raw, out Guid id)) return null;
        var doc = await session.LoadAsync<TDoc>(id, ct);
        return doc is not null && !isDeleted(doc) ? doc : null;
    }

    /// <summary>
    /// Guards the id-matched update of a type whose natural key CANNOT be renamed through
    /// its canonical update op (app slug, client id, scope/api name, provider slug). The id
    /// and the key then name two different entities, which is never a silent merge — the
    /// entry fails with both ways out spelled out.
    /// </summary>
    private static void EnsureRenameable(
        bool renameable, string manifestKey, string liveKey, string keyField, string ctx)
    {
        if (renameable || string.Equals(manifestKey, liveKey, StringComparison.OrdinalIgnoreCase)) return;
        throw new ManifestApplyException(ctx, [Error.Validation("Manifest.ImmutableKey",
            $"{ctx}: the pinned Id belongs to '{liveKey}', but {keyField} is immutable — it cannot be renamed to '{manifestKey}'. Fix the {keyField} to match, or remove the Id to create a separate entity.")]);
    }

    /// <summary>
    /// Users are the ONE type whose deletion is not a bare soft-delete: it runs the account
    /// lifecycle (recycle bin, grace period, GDPR purge). Reviving that stream from a
    /// manifest would bypass it, so a pinned id belonging to a binned user is refused with
    /// the way out named explicitly — restore the user, then re-apply (the apply then
    /// UPDATES the restored user, id intact).
    /// </summary>
    private static async Task<Guid?> ResolvePinnedUserAsync(
        IDocumentSession session, string? raw, string ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!ShortGuid.TryParse(raw, out Guid id))
            throw new ManifestApplyException(ctx, [Error.Validation("User.InvalidPinnedId",
                $"Pinned id '{raw}' is not a valid Guid or ShortGuid.")]);
        if (await session.Events.FetchStreamStateAsync(id, ct) is null) return id;

        var person = await session.LoadAsync<Person>(id, ct);
        throw new ManifestApplyException(ctx, [Error.Conflict("User.PinnedIdTaken",
            person is { IsDeleted: true }
                ? $"{ctx}: the pinned id '{raw}' belongs to a deleted user in the recycle bin. Restore that user first, then re-apply — the apply then updates it and the id stays the same."
                : $"{ctx}: the pinned id '{raw}' is already used by a live entity (or one of a different type) in this realm.")]);
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
