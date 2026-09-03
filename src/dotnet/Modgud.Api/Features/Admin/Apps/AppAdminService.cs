using ErrorOr;
using Marten;
using Modgud.Application.DTOs.Applications;
using Modgud.Authentication.Applications;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Apis;
using Modgud.Authorization.Roles;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// The single canonical write path for creating and updating <see cref="App"/> records,
/// shared by <see cref="AppsEndpoints"/> and the realm-provisioning applier so the manual
/// path and the manifest path can never diverge. Returns <see cref="ErrorOr{T}"/> so the
/// endpoint maps it to HTTP while the applier consumes it directly. The injected
/// <see cref="IDocumentSession"/> is tenant-scoped, so a call lands in whatever realm
/// the ambient <c>TenantContext</c> selects.
///
/// <para>An App is ONE resource: when <see cref="CreateAppDto.Settings"/> /
/// <see cref="UpdateAppDto.Settings"/> is supplied, the per-App ADR-0011 settings override
/// is written in the SAME tenant transaction as the App aggregate (atomic). The only piece
/// that cannot be — the <c>Origin</c> subdomain, which drives the GLOBAL host→App routing
/// map in a different database — is validated up-front (so an invalid subdomain rejects the
/// whole call before any commit) and its routing applied right after the atomic commit. The
/// applier passes no settings, so its behaviour is unchanged.</para>
/// </summary>
public sealed class AppAdminService(IDocumentSession session, IApplicationSettingsService settingsSvc)
{
    public async Task<ErrorOr<App>> CreateAppAsync(CreateAppDto dto, CancellationToken ct = default)
    {
        if (!AppSlugRules.IsValidFormat(dto.Slug))
            return Error.Validation("App.InvalidSlug",
                "Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens.");

        if (AppSlugRules.IsReserved(dto.Slug))
            return Error.Validation("App.ReservedSlug", $"The slug '{dto.Slug}' is reserved.");

        if (string.IsNullOrWhiteSpace(dto.DisplayName))
            return Error.Validation("App.DisplayNameRequired", "DisplayName is required.");

        var duplicate = await session.Query<App>()
            .Where(a => a.Slug == dto.Slug && !a.IsDeleted)
            .AnyAsync(ct);
        if (duplicate)
            return Error.Conflict("App.DuplicateSlug", $"An app with slug '{dto.Slug}' already exists.");

        var permissions = NormalizePermissions(dto.Permissions, existingByKey: null);
        if (permissions.IsError) return permissions.Errors;

        var pinned = await Modgud.Application.Services.PinnedEntityId.ResolveAsync<App>(
            session, dto.Id, "App", a => a.IsDeleted, ct);
        if (pinned.IsError) return pinned.Errors;
        var id = pinned.Value.Id ?? Guid.NewGuid();
        var createdEvent = new AppCreatedEvent(
            Id: id,
            Slug: dto.Slug,
            DisplayName: dto.DisplayName,
            Description: dto.Description,
            Permissions: permissions.Value,
            IsSystem: false);
        // Revive: append onto the soft-deleted stream instead of starting a new one.
        if (pinned.Value.Revive)
            session.Events.Append(id, createdEvent);
        else
            session.Events.StartStream<App>(id, createdEvent);

        // App + its settings override commit together (atomic) — see class summary.
        if (dto.Settings is not null)
        {
            var staged = await StageSettingsAsync(id, dto.Settings, ct);
            if (staged.IsError) return staged.Errors;
        }

        await session.SaveChangesAsync(ct);

        var originApplied = await ApplyOriginIfAnyAsync(id, dto.Settings, ct);
        if (originApplied.IsError) return originApplied.Errors;

        return (await session.LoadAsync<App>(id, ct))!;
    }

    /// <summary>
    /// The single canonical update path for an existing <see cref="App"/> — display name,
    /// description, and the permission catalog. Mirrors the create path's validation and
    /// adds the catalog-edit safety net: removing a catalog entry that is still referenced
    /// by a role or resource server is refused with a <see cref="ErrorType.Conflict"/>
    /// whose <c>Metadata["blockers"]</c> carries the structured reference list (so the
    /// admin endpoint can render its rich 409 body and the applier can surface the cause).
    /// </summary>
    public async Task<ErrorOr<App>> UpdateAppAsync(Guid id, UpdateAppDto dto, CancellationToken ct = default)
    {
        var app = await session.LoadAsync<App>(id, ct);
        if (app is null || app.IsDeleted)
            return Error.NotFound("App.NotFound", "App not found.");

        if (string.IsNullOrWhiteSpace(dto.DisplayName))
            return Error.Validation("App.DisplayNameRequired", "DisplayName is required.");

        // Existing-permission lookup by id keeps stable identities across updates: an entry
        // already present by id retains it, an entry without an id gets a fresh one.
        var existingByKey = app.Permissions.ToDictionary(p => p.Id, p => p);
        var permissions = NormalizePermissions(dto.Permissions, existingByKey);
        if (permissions.IsError) return permissions.Errors;

        // Detect catalog deletions that would orphan FKs in PermissionRole.PermissionIds or
        // OAuthApiState.PermissionIds. Removing a still-referenced entry is a silent
        // permission revocation in disguise — refuse with 409 + what's blocking.
        var newIds = permissions.Value.Select(p => p.Id).ToHashSet();
        var removedIds = app.Permissions.Where(p => !newIds.Contains(p.Id)).ToList();
        if (removedIds.Count > 0)
        {
            var blockers = await FindReferencesAsync(removedIds.Select(p => p.Id).ToList(), session, ct);
            if (blockers.Count > 0)
            {
                var payload = blockers.Select(b => new AppCatalogBlocker(
                    new BuildingBlocks.Helper.ShortGuid(b.PermissionId).ToString(),
                    removedIds.First(p => p.Id == b.PermissionId).ToPermissionString(),
                    b.RoleNames,
                    b.OAuthApiNames)).ToList();
                return Error.Conflict("App.CatalogEntriesReferenced",
                    "Cannot remove catalog entries that are still referenced by roles or resource servers. Detach them first.",
                    new Dictionary<string, object> { ["blockers"] = payload });
            }
        }

        session.Events.Append(id, new AppUpdatedEvent(
            id, dto.DisplayName, dto.Description, permissions.Value));

        // App + its settings override commit together (atomic) — see class summary.
        if (dto.Settings is not null)
        {
            var staged = await StageSettingsAsync(id, dto.Settings, ct);
            if (staged.IsError) return staged.Errors;
        }

        await session.SaveChangesAsync(ct);

        var originApplied = await ApplyOriginIfAnyAsync(id, dto.Settings, ct);
        if (originApplied.IsError) return originApplied.Errors;

        return (await session.LoadAsync<App>(id, ct))!;
    }

    /// <summary>
    /// Validates the Origin subdomain up-front (so an invalid one rejects the whole
    /// create/update before any commit) and stages every other settings section onto the
    /// shared session — committed atomically with the App by the caller's SaveChangesAsync.
    /// </summary>
    private async Task<ErrorOr<Success>> StageSettingsAsync(Guid appId, ApplicationSettingsDto settings, CancellationToken ct)
    {
        if (settings.Origin is not null)
        {
            var validOrigin = await settingsSvc.ValidateOriginAsync(appId, settings.Origin.Subdomain, ct);
            if (validOrigin.IsError) return validOrigin.Errors;
        }
        var staged = await settingsSvc.StageNonOriginAsync(appId, settings, ct);
        return staged.IsError ? staged.Errors : Result.Success;
    }

    /// <summary>
    /// Applies the Origin subdomain → GLOBAL host routing AFTER the atomic tenant commit
    /// (the routing map lives in a different database, so it can't be in the tenant
    /// transaction). Already validated in <see cref="StageSettingsAsync"/>, so this only
    /// fails on a race or infrastructure error — and then the App + its other settings are
    /// already persisted (a valid state; only the subdomain route is missing).
    /// </summary>
    private async Task<ErrorOr<Success>> ApplyOriginIfAnyAsync(Guid appId, ApplicationSettingsDto? settings, CancellationToken ct)
    {
        if (settings?.Origin is null) return Result.Success;
        var r = await settingsSvc.PatchAsync(appId, new ApplicationSettingsDto { Origin = settings.Origin }, ct);
        return r.IsError ? r.Errors : Result.Success;
    }

    /// <summary>
    /// The single canonical delete path for an <see cref="App"/>, shared by
    /// <see cref="AppsEndpoints"/> and the realm-provisioning applier's prune. Refuses the
    /// system app, and refuses an App that is still referenced — by a role / resource server
    /// linked directly to it (<c>PermissionRole.AppId</c> / <c>OAuthApiState.AppId</c>) or by
    /// an FK into any of its catalog entries — because deleting it would silently revoke those
    /// grants. The structured reference list rides through <c>Metadata["appReferences"]</c>
    /// so the admin endpoint can render its rich 409 body (the same one
    /// <c>AppDetails.vue</c> consumes).
    /// </summary>
    public async Task<ErrorOr<Success>> DeleteAppAsync(Guid id, CancellationToken ct = default)
    {
        var app = await session.LoadAsync<App>(id, ct);
        if (app is null || app.IsDeleted)
            return Error.NotFound("App.NotFound", "App not found.");

        if (app.IsSystem)
            return Error.Validation("App.CannotDeleteSystemApp",
                $"The system app '{app.Slug}' cannot be deleted.");

        // App-level delete-block: if any role or resource-server FKs into this App's catalog
        // (or directly into the App via PermissionRole.AppId / OAuthApiState.AppId), refuse.
        // Same rationale as the per-entry catalog block: deleting an App with live grants is a
        // silent revoke.
        var allCatalogIds = app.Permissions.Select(p => p.Id).ToList();
        var blockingByPermissionId = allCatalogIds.Count > 0
            ? await FindReferencesAsync(allCatalogIds, session, ct)
            : [];
        var rolesByApp = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.AppId == app.Id)
            .Select(r => r.Name)
            .ToListAsync(ct);
        var apisByApp = await session.Query<OAuthApiState>()
            .Where(a => !a.IsDeleted && a.AppId == app.Id)
            .Select(a => a.Name)
            .ToListAsync(ct);

        if (blockingByPermissionId.Count > 0 || rolesByApp.Count > 0 || apisByApp.Count > 0)
        {
            var catalogBlockers = blockingByPermissionId.Select(b => new AppCatalogBlocker(
                new BuildingBlocks.Helper.ShortGuid(b.PermissionId).ToString(),
                app.Permissions.First(p => p.Id == b.PermissionId).ToPermissionString(),
                b.RoleNames,
                b.OAuthApiNames)).ToList();
            return Error.Conflict("App.HasReferences",
                "Cannot delete an App that's still referenced. Detach roles and resource servers first.",
                new Dictionary<string, object>
                {
                    ["appReferences"] = new AppReferenceBlockers(rolesByApp.ToList(), apisByApp.ToList(), catalogBlockers),
                });
        }

        // The global host map lives outside the tenant transaction. Remove it first:
        // a failed tenant commit then merely leaves a live App without its vanity host,
        // whereas the opposite order could route traffic to a deleted App indefinitely.
        var originRemoved = await settingsSvc.RemoveOriginRoutingAsync(id, ct);
        if (originRemoved.IsError) return originRemoved.Errors;

        session.Delete<ApplicationSettings>(id);
        session.Events.Append(id, new AppDeletedEvent(id));
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }

    /// <summary>
    /// Validates and normalises the permission catalog off a create / update payload:
    /// parses incoming ids (ShortGuid → Guid, minting a fresh one when absent), dedupes
    /// by (Resource, Action), enforces the segment grammar and rejects the reserved
    /// <c>realm:admin</c> bypass. Shared by create (here) and the AppsEndpoints update
    /// path so there is one normalisation rule.
    /// </summary>
    internal static ErrorOr<List<AppPermission>> NormalizePermissions(
        List<AppPermissionDto>? payload,
        IReadOnlyDictionary<Guid, AppPermission>? existingByKey)
    {
        var input = payload ?? [];
        var normalised = new List<AppPermission>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in input)
        {
            var resource = entry.Resource?.Trim() ?? string.Empty;
            var action = entry.Action?.Trim() ?? string.Empty;

            if (!AppPermissionRules.IsValidSegment(resource) ||
                !AppPermissionRules.IsValidSegment(action))
            {
                return Error.Validation("App.InvalidPermissionSegment",
                    $"Permission '{resource}:{action}' is invalid — both segments must match ^[a-z0-9-]+$.");
            }

            // realm:admin is the synthetic realm-wide bypass — it must never be a catalog
            // entry (audit H1, vector 3). Conferring realm:admin is reserved to a role's
            // IsRealmAdmin flag, which is itself gated on the caller holding realm:admin.
            if (AppPermissionRules.IsReservedBypass(resource, action))
            {
                return Error.Validation("App.ReservedPermission",
                    "The permission 'realm:admin' is reserved — it is the realm-wide bypass and cannot be a catalog entry. Use a role's IsRealmAdmin flag instead.");
            }

            var key = $"{resource}:{action}";
            if (!seen.Add(key))
                continue; // silently drop exact duplicates

            // Explicit id wins (rename / detached-replay path); otherwise mint a new one.
            var id = Guid.NewGuid();
            if (!string.IsNullOrEmpty(entry.Id) && BuildingBlocks.Helper.ShortGuid.TryParse(entry.Id, out Guid parsed))
                id = parsed;

            var description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description.Trim();
            normalised.Add(new AppPermission(id, resource, action, description));
        }

        return normalised;
    }

    /// <summary>
    /// Per-permission-id reference summary used by the catalog editor's delete-block
    /// panel. Only entries with at least one referencing role or RS are returned.
    /// </summary>
    internal sealed record PermissionReference(Guid PermissionId, List<string> RoleNames, List<string> OAuthApiNames);

    /// <summary>
    /// Finds every <see cref="PermissionRole"/> and <see cref="OAuthApiState"/> that
    /// references any of the supplied permission ids in their respective
    /// <c>PermissionIds</c> FK list. Returns one entry per permission-id that has at least
    /// one referencing row — empty list = safe to remove. Shared by the catalog update
    /// (here) and the App-delete block in <see cref="AppsEndpoints"/>.
    /// </summary>
    internal static async Task<List<PermissionReference>> FindReferencesAsync(
        List<Guid> permissionIds, IDocumentSession session, CancellationToken ct = default)
    {
        if (permissionIds.Count == 0) return [];

        // For our small catalogs it's acceptable to load every role/api with any non-empty
        // PermissionIds and filter in memory. Tenant DBs aren't huge here.
        var roles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.PermissionIds.Any())
            .ToListAsync(ct);
        var apis = await session.Query<OAuthApiState>()
            .Where(a => !a.IsDeleted && a.PermissionIds.Any())
            .ToListAsync(ct);

        var result = new List<PermissionReference>();
        foreach (var pid in permissionIds)
        {
            var roleNames = roles.Where(r => r.PermissionIds.Contains(pid)).Select(r => r.Name).ToList();
            var apiNames = apis.Where(a => a.PermissionIds.Contains(pid)).Select(a => a.Name).ToList();
            if (roleNames.Count > 0 || apiNames.Count > 0)
                result.Add(new PermissionReference(pid, roleNames, apiNames));
        }
        return result;
    }
}

/// <summary>
/// The rich blocker shape surfaced in the <c>App.CatalogEntriesReferenced</c> 409 body —
/// one entry per still-referenced catalog id the update tried to remove. Carried through
/// <see cref="Error.Metadata"/> so <see cref="AppsEndpoints"/> can render it verbatim and
/// the admin SPA's <c>AppDetails.vue</c> delete-block panel keeps working.
/// </summary>
public sealed record AppCatalogBlocker(
    string PermissionId,
    string Permission,
    List<string> ReferencedByRoles,
    List<string> ReferencedByResourceServers);

/// <summary>
/// The rich blocker shape surfaced in the <c>App.HasReferences</c> 409 body when a delete is
/// refused — the roles / resource servers linked directly to the App plus the per-catalog-entry
/// references. Carried through <see cref="Error.Metadata"/> so <see cref="AppsEndpoints"/> can
/// render it verbatim for <c>AppDetails.vue</c>'s delete-block panel.
/// </summary>
public sealed record AppReferenceBlockers(
    List<string> ReferencedByRoles,
    List<string> ReferencedByResourceServers,
    List<AppCatalogBlocker> CatalogEntryReferences);
