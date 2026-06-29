using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Api.Features.Admin.Apps;
using Modgud.Api.Features.Roles;
using Modgud.Api.Features.Users.Commands;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.User;
using Modgud.Application.Services;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Wolverine;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Applies a <see cref="RealmManifest"/> in-process by calling the existing canonical
/// application operations — the engine behind declarative realm provisioning.
///
/// <para>Invariant: ZERO new write logic. Each section is dispatched to the SAME
/// operation the admin UI / admin API uses (<see cref="IRealmProvisioningService"/>,
/// <see cref="IRealmSettingsService"/>, <see cref="AppAdminService"/>,
/// <see cref="OAuthAdminService"/>, <see cref="RoleAdminService"/>, the user/group
/// Wolverine commands), so the manifest path and the manual path can never drift.</para>
///
/// <para>Tenant routing: the realm shell is created via the global store, then the
/// per-tenant config runs inside <c>TenantContext.Enter(slug)</c> + a fresh DI scope —
/// <c>TenantedSessionFactory</c> prefers the AsyncLocal <c>TenantContext</c> over the
/// ambient (control-plane) <c>HttpContext</c>. Wolverine handlers resolve their session
/// from the message-envelope tenant, so the user/group commands use
/// <c>InvokeForTenantAsync(slug, ...)</c>.</para>
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
                Enabled = api.Enabled,
                Scopes = api.Scopes,
                UserClaims = api.UserClaims,
                AppId = ResolveAppId(apps, api.App, $"api '{api.Name}'"),
                PermissionIds = ResolvePermissionIds(apps, api.App, api.Permissions, $"api '{api.Name}'"),
                AllowDynamicRegistration = api.AllowDynamicRegistration,
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
                Enabled = s.Enabled,
                Required = s.Required,
                Emphasize = s.Emphasize,
                ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
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
                Enabled = c.Enabled,
                RequireConsent = c.RequireConsent,
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

        // ── Users — Wolverine commands, dispatched for the realm tenant ───────────
        var bus = sp.GetRequiredService<IMessageBus>();
        foreach (var u in manifest.Users)
        {
            var cmd = new CreateUserCommand(u.Firstname, u.Lastname, u.Acronym, u.Email,
                u.UserName ?? string.Empty, u.Password, u.EmailConfirmed);
            var created = await bus.InvokeForTenantAsync<ErrorOr<UserDto>>(slug, cmd, ct);
            EnsureOk(created, $"user '{u.Email}'");
            if (ShortGuid.TryParse(created.Value.Id, out Guid uid))
                userIds[u.ResolveKey()] = uid;
        }

        // ── Groups — committed via a PLAIN tenant-scoped session (NOT the Wolverine
        //    outbox session). InvokeForTenantAsync would enroll the Wolverine outbox, and
        //    the durable-inbox auto-membership event forwarding (ReferenceSync) would try
        //    to write wolverine_incoming_envelopes in the tenant DB, which a fresh realm
        //    lacks. A plain session skips that forwarding (auto-membership re-derives at
        //    login). We call the canonical CreateGroupHandler directly with this session.
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
                    g.BoundTo, g.ExternallyDrivable, CallerIsRealmAdmin: true);
                EnsureOk(await groupHandler.Handle(cmd, ct), $"group '{g.Name}'");
            }
        }

        return secrets;
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
