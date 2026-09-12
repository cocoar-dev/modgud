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
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.User;
using Modgud.Application.Services;
using Modgud.Authentication.Api.Admin.LoginProviders.Commands;
using Modgud.Authentication.Api.Users;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;
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
/// <para>A manifest describes CONTENT only — it carries no realm shell, so the target is
/// named by the caller (the route) and the same file applies to any realm. Creating a
/// realm is a separate operation (<c>POST /api/admin/realms</c>); this type only ever
/// fills an existing one.</para>
///
/// <para>Tenant routing: the per-tenant config runs inside <c>TenantContext.Enter(slug)</c>
/// + a fresh DI scope —
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
    /// Updates the realm named by <paramref name="slug"/> in place; it MUST already exist
    /// (create it via <c>POST /api/admin/realms</c> first). Each entity in the
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
    /// <para>Atomicity (ADR-0017 Phase 0): the whole update runs inside ONE
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
    /// <para><paramref name="deletions"/> (ADR-0017 staged deletes) are prune's per-entity
    /// counterpart: only the listed (section, key) targets are deleted, through the SAME
    /// canonical delete ops, guards and reverse-dependency order — inside the same apply
    /// transaction. Protection violations throw (the draft plan gate flags them as errors
    /// beforehand).</para>
    /// </summary>
    public async Task<ErrorOr<RealmImportResult>> UpdateRealmAsync(
        string slug, RealmManifest manifest, bool prune = false,
        IReadOnlyCollection<RealmDraftDeletion>? deletions = null, CancellationToken ct = default)
    {
        var realm = await realms.GetRealmBySlugAsync(slug, ct);
        if (realm is null)
            return Error.NotFound("Realm.NotFound",
                $"Realm '{slug}' does not exist. Create it first (POST /api/admin/realms), then apply.");

        // The document has to make sense on its own before a transaction is opened: a
        // handle declared twice, a reference to a handle nothing declares, a reference
        // carrying only a name. All of those are the file contradicting itself (ADR 0024),
        // and none of them depend on the realm.
        var validated = ManifestIdentity.Validate(manifest);
        if (validated.IsError) return validated.Errors;
        var identity = validated.Value;

        var skips = new ManifestReferenceSkips();
        try
        {
            var secrets = await ApplyTenantUpdateAsync(slug, manifest, prune, deletions, skips, identity, ct);
            logger.LogInformation(
                "Updated realm {Slug}: {Apps} apps, {Apis} apis, {Scopes} scopes, {Clients} clients, {Roles} roles, {Users} users, {Groups} groups, {Providers} login providers, {Positions} positions (in-place merge).",
                slug, manifest.Apps.Count, manifest.Apis.Count, manifest.Scopes.Count,
                manifest.Clients.Count, manifest.Roles.Count, manifest.Users.Count, manifest.Groups.Count,
                manifest.LoginProviders.Count, manifest.Positions.Count);
            if (skips.Skips.Count > 0)
            {
                logger.LogWarning(
                    "Apply of realm {Slug} skipped {Count} unresolvable manifest reference(s): {Skips}",
                    slug, skips.Skips.Count, string.Join(" | ", skips.Skips));
            }
            return new RealmImportResult
            {
                Slug = slug,
                PrimaryDomain = realm.PrimaryDomain,
                ClientSecrets = secrets,
                SkippedReferences = [.. skips.Skips],
                AssignedIds = identity.Assigned.ToDictionary(
                    kv => kv.Key, kv => new ShortGuid(kv.Value).ToString(), StringComparer.Ordinal),
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

    /// <summary>
    /// In-place upsert of every entity in the manifest against an already-provisioned realm:
    /// reads current state by natural key and dispatches to the canonical Update op when the
    /// entity exists, the Create op when it doesn't. This is the ONLY apply path — a fresh
    /// realm is created first (POST /api/admin/realms) and then filled through exactly these
    /// upserts, so provisioning and merging can never drift apart. See
    /// <see cref="UpdateRealmAsync"/> for the field-level merge semantics.
    /// </summary>
    private async Task<Dictionary<string, string>> ApplyTenantUpdateAsync(
        string slug, RealmManifest manifest, bool prune,
        IReadOnlyCollection<RealmDraftDeletion>? deletions, ManifestReferenceSkips skips,
        ManifestIdentity identity, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        using var _ = TenantContext.Enter(slug);

        // ADR-0017 Phase 0: one transaction for the whole apply. Activate() installs
        // the ambient marker synchronously so TenantedSessionFactory binds every
        // session below to this transaction and the Deferring* revokers collect
        // their cascades instead of running them. Commit happens after the last
        // section; any ManifestApplyException unwinds through the usings and the
        // DisposeAsync rolls everything back.
        await using var applyTx = await TenantApplyTransaction.BeginAsync(store, slug, ct);
        using (applyTx.Activate())
        {
            await ApplyTenantUpdateSectionsAsync(manifest, prune, deletions, secrets, skips, identity, ct);
            await applyTx.CommitAsync(ct);
        }

        // Consequences (token revocation, staffing-session termination) run only now,
        // in fresh scopes against the committed state; the ambient marker is gone.
        await applyTx.RunDeferredAsync(scopeFactory, logger, ct);

        return secrets;
    }

    private async Task ApplyTenantUpdateSectionsAsync(
        RealmManifest manifest, bool prune, IReadOnlyCollection<RealmDraftDeletion>? deletions,
        Dictionary<string, string> secrets, ManifestReferenceSkips skips, ManifestIdentity identity,
        CancellationToken ct)
    {
        // slug → App (id + permission catalog). App slugs are the permission VOCABULARY —
        // they appear in every permission string and every role key — so they stay names;
        // ADR 0024 is about which ENTITY an entry means, and that is the id below.
        var apps = new Dictionary<string, App>(StringComparer.Ordinal);

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
            var appCtx = $"app '{app.Slug}'";
            App result;
            // ADR 0024: the Id names the entity, and nothing else does. Without one this
            // entry CREATES — and a taken slug then fails loudly (App.DuplicateSlug)
            // instead of quietly rewriting whatever app happens to carry that slug here.
            var current = await MatchByPinnedIdAsync<App>(session, app.Id, a => a.IsDeleted, ct);
            // An app slug can't be renamed through the canonical update, so an id naming
            // a differently-slugged app is an error rather than a silent merge.
            if (current is not null)
                EnsureRenameable(false, app.Slug, current.Slug, "Slug", appCtx);
            if (current is not null)
            {
                // v2 merge-patch: an absent catalog keeps the current one verbatim; a
                // present catalog (incl. []) replaces. A catalog entry keeps its id, and
                // its ID is what identifies it (ADR 0024) — carrying that is what makes a
                // RENAME a rename. Falling back to resource:action keeps an entry written
                // without an id (a hand-written file) from looking "removed + re-added",
                // which would trip the catalog-delete guard on roles/RSes holding the FK.
                var live = current.Permissions.ToDictionary(p => p.Id);
                var byKey = current.Permissions.ToDictionary(p => $"{p.Resource}:{p.Action}", p => p.Id);
                string? CatalogId(RealmManifestPermission p)
                {
                    if (ShortGuid.TryParse(p.Id ?? string.Empty, out Guid pinned) && live.ContainsKey(pinned))
                        return new ShortGuid(pinned).ToString();
                    return byKey.TryGetValue($"{p.Resource}:{p.Action}", out var existingId)
                        ? new ShortGuid(existingId).ToString()
                        : null;
                }
                var permissions = app.Permissions is null
                    ? current.Permissions.Select(p => new AppPermissionDto(
                        new ShortGuid(p.Id).ToString(), p.Resource, p.Action, p.Description)).ToList()
                    : app.Permissions.Select(p => new AppPermissionDto(
                        CatalogId(p), p.Resource, p.Action, p.Description)).ToList();
                var description = app.Description.HasValue ? app.Description.Value : current.Description;
                var updated = await appAdmin.UpdateAppAsync(current.Id,
                    new UpdateAppDto(app.DisplayName, description, permissions,
                        await KeepRealmLocalSettingsAsync(sp, current.Id, app.Settings, ct)), ct);

                EnsureOk(updated, appCtx);
                result = updated.Value;
            }
            else
            {
                var permissions = (app.Permissions ?? [])
                    .Select(p => new AppPermissionDto(null, p.Resource, p.Action, p.Description)).ToList();
                var created = await appAdmin.CreateAppAsync(
                    new CreateAppDto(app.Slug, app.DisplayName, OrNull(app.Description), permissions,
                        app.Settings, ManifestHandle.AsPinnedId(app.Id)), ct);
                EnsureOk(created, appCtx);
                result = created.Value;
            }
            apps[app.Slug] = result;
            identity.Assign(app.Id, result.Id);
            identity.Applied(ManifestIdentity.Sections.Apps, result.Id);
        }

        var oauth = sp.GetRequiredService<OAuthAdminService>();

        // ── OAuth APIs (identity = Id; the Name is the audience it carries) ────────
        foreach (var api in manifest.Apis)
        {
            var ctx = $"api '{api.Name}'";
            var existing = await MatchByPinnedIdAsync<OAuthApiState>(session, api.Id, x => x.IsDeleted, ct);
            // The audience IS the API's identity for every token consumer — immutable.
            if (existing is not null) EnsureRenameable(false, api.Name, existing.Name, "Name", ctx);
            if (existing is null)
            {
                var created = await oauth.CreateApiAsync(new CreateOAuthApiDto
                {
                    Id = ManifestHandle.AsPinnedId(api.Id),
                    Name = api.Name,
                    DisplayName = OrNull(api.DisplayName),
                    Description = OrNull(api.Description),
                    Enabled = api.Enabled ?? true,
                    Scopes = api.Scopes ?? [],
                    UserClaims = api.UserClaims ?? [],
                    AppId = ResolveAppId(apps, OrNull(api.App), ctx, skips),
                    PermissionIds = ResolvePermissionIds(apps, OrNull(api.App), api.Permissions, ctx, skips) ?? [],
                    AllowDynamicRegistration = api.AllowDynamicRegistration ?? false,
                }, ct);
                EnsureOk(created, ctx);
                RegisterApplied(identity, ManifestIdentity.Sections.Apis, api.Id, created.Value.Id, ctx);
            }
            else
            {
                identity.Assign(api.Id, existing.Id);
                identity.Applied(ManifestIdentity.Sections.Apis, existing.Id);
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
                        ? new Optional<string?>(ResolveAppId(apps, api.App.Value, ctx, skips))
                        : default,
                    // null = unchanged, which is also what an all-unresolvable list becomes.
                    PermissionIds = api.Permissions is null
                        ? null
                        : ResolvePermissionIds(apps, OrNull(api.App), api.Permissions, ctx, skips),
                    AllowDynamicRegistration = api.AllowDynamicRegistration,
                }, ct), ctx);
            }
        }

        // ── OAuth scopes (identity = Id; the Name is what clients request) ─────────
        foreach (var s in manifest.Scopes)
        {
            var ctx = $"scope '{s.Name}'";
            var existing = await MatchByPinnedIdAsync<OAuthScopeState>(session, s.Id, x => x.IsDeleted, ct);
            if (existing is not null) EnsureRenameable(false, s.Name, existing.Name, "Name", ctx);
            if (existing is null)
            {
                var created = await oauth.CreateScopeAsync(new CreateOAuthScopeDto
                {
                    Id = ManifestHandle.AsPinnedId(s.Id),
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
                    AppId = ResolveAppId(apps, OrNull(s.App), ctx, skips),
                }, ct);
                EnsureOk(created, ctx);
                RegisterApplied(identity, ManifestIdentity.Sections.Scopes, s.Id, created.Value.Id, ctx);
            }
            else
            {
                identity.Assign(s.Id, existing.Id);
                identity.Applied(ManifestIdentity.Sections.Scopes, existing.Id);
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
                        ? new Optional<string?>(ResolveAppId(apps, s.App.Value, ctx, skips))
                        : default,
                }, ct), ctx);
            }
        }

        // ── OAuth clients (identity = Id; the ClientId is what the client sends) ───
        foreach (var c in manifest.Clients)
        {
            var ctx = $"client '{c.ClientId}'";
            var existing = await MatchByPinnedIdAsync<OAuthApplicationState>(session, c.Id, x => x.IsDeleted, ct);
            if (existing is not null) EnsureRenameable(false, c.ClientId, existing.ClientId, "ClientId", ctx);
            if (existing is null)
            {
                var created = await oauth.CreateClientAsync(BuildClientCreateDto(c, apps, ctx, skips), ct);
                EnsureOk(created, ctx);
                if (created.Value.ClientSecret is not null)
                    secrets[c.ClientId] = created.Value.ClientSecret;
                RegisterApplied(identity, ManifestIdentity.Sections.Clients, c.Id, created.Value.Client.Id, ctx);
            }
            else
            {
                identity.Assign(c.Id, existing.Id);
                identity.Applied(ManifestIdentity.Sections.Clients, existing.Id);
                // ClientType + secret are immutable through the canonical update path; an
                // existing client keeps its secret (rotate via the dedicated endpoint).
                EnsureOk(await oauth.UpdateClientAsync(
                    existing.Id.ToString(), BuildClientUpdateDto(c, apps, ctx, skips), ct), ctx);
            }
        }

        // ── Login providers (identity = Id; the Slug owns the callback URLs) ───────
        foreach (var lp in manifest.LoginProviders)
        {
            var ctx = $"login provider '{lp.Slug}'";
            var existing = await MatchByPinnedIdAsync<LoginProvider>(session, lp.Id, x => x.IsDeleted, ct);
            // The slug owns the provider's callback URLs — immutable after create.
            if (existing is not null) EnsureRenameable(false, lp.Slug, existing.Slug, "Slug", ctx);
            if (existing is null)
            {
                var createdProvider = await BuildCreateProviderHandler(sp).Handle(
                    BuildCreateProviderCommand(lp, ctx,
                        await ResolvePinnedAsync<LoginProvider>(
                            session, ManifestHandle.AsPinnedId(lp.Id), "LoginProvider", ctx, x => x.IsDeleted, ct)), ct);
                EnsureOk(createdProvider, ctx);
                identity.Assign(lp.Id, createdProvider.Value.Id);
                identity.Applied(ManifestIdentity.Sections.LoginProviders, createdProvider.Value.Id);
                continue;
            }
            identity.Assign(lp.Id, existing.Id);
            identity.Applied(ManifestIdentity.Sections.LoginProviders, existing.Id);

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

        // ── Roles (identity = Id; a role's name is unique per App only) ────────────
        var roleAdmin = sp.GetRequiredService<RoleAdminService>();
        foreach (var r in manifest.Roles)
        {
            var ctx = $"role '{r.NaturalKey}'";
            // ADR 0024: the Id names the role, and an id-matched entry RENAMES it. Without
            // an id this creates — `app/name` is not identity, because the app it names in
            // THIS realm need not be the app the file was written against.
            var existing = await MatchByPinnedIdAsync<PermissionRole>(session, r.Id, x => x.IsDeleted, ct);
            // v2 merge-patch: absent fields keep the existing role's values (the
            // canonical update is a full payload replace, so merge here).
            var isRealmAdmin = r.IsRealmAdmin ?? existing?.IsRealmAdmin ?? false;

            // A role's app is not an ordinary skippable reference: the app OWNS the catalog
            // the role grants from, and the domain refuses a role that is neither app-linked
            // nor realm-admin. So an app this realm does not have cannot simply "drop out" —
            // an existing role keeps the app it already has, and a NEW one cannot be created
            // at all, which makes the whole role the thing that gets skipped.
            var appId = r.App is null
                ? isRealmAdmin || existing?.AppId is not { } keptId ? null : new ShortGuid(keptId).ToString()
                : ResolveAppId(apps, r.App, ctx, skips)
                  ?? (existing?.AppId is { } fallbackId ? new ShortGuid(fallbackId).ToString() : null);

            if (!isRealmAdmin && appId is null && r.App is not null)
            {
                skips.Skip(ctx, $"app '{r.App}'",
                    "no such app in this realm, and a role must belong to one — the whole role is skipped");
                continue;
            }

            var payload = new RolePayload(
                r.Name,
                r.Description.HasValue ? r.Description.Value : existing?.Description,
                appId,
                isRealmAdmin,
                // Absent permissions keep the stored set — and so does a list that resolved
                // to nothing (its app is not in this realm), which is why the null coalesces
                // onto the existing ids rather than onto an empty list.
                r.Permissions is null && existing is not null
                    ? existing.PermissionIds.Select(pid => new ShortGuid(pid).ToString()).ToList()
                    : ResolvePermissionIds(apps, r.App, r.Permissions ?? [], ctx, skips)
                      ?? existing?.PermissionIds.Select(pid => new ShortGuid(pid).ToString()).ToList()
                      ?? [],
                ManifestHandle.AsPinnedId(r.Id));
            // Control-plane provisioning is trusted, so the realm-admin guard is satisfied.
            ErrorOr<PermissionRole> result = existing is null
                ? await roleAdmin.CreateRoleAsync(payload, callerIsRealmAdmin: true, ct)
                : await roleAdmin.UpdateRoleAsync(existing.Id, payload, callerIsRealmAdmin: true, ct);
            EnsureOk(result, ctx);
            identity.Assign(r.Id, result.Value.Id);
            identity.Applied(ManifestIdentity.Sections.Roles, result.Value.Id);
        }

        // ── Users (identity = Id; email and username are mutable profile fields) ───
        var setPassword = sp.GetRequiredService<SetUserPasswordHandler>();
        var createUser = new CreateUserHandler(
            session,
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IApplicationSettingsResolver>());
        foreach (var u in manifest.Users)
        {
            var ctx = $"user '{u.Email}'";
            // ADR 0024: an id-matched entry updates (and renames) that person; without an
            // id the entry creates, and a taken email fails loudly. Matching by email would
            // hand a stranger's account to whoever wrote the file.
            var existing = await MatchByPinnedIdAsync<Person>(session, u.Id, x => x.IsDeleted, ct);

            Guid? uid;
            if (existing is null)
            {
                var createCmd = new CreateUserCommand(OrNull(u.Firstname), OrNull(u.Lastname), OrNull(u.Acronym),
                    u.Email, u.UserName ?? string.Empty, u.Password, u.EmailConfirmed ?? false,
                    IsActive: u.IsActive ?? true,
                    Id: await ResolvePinnedUserAsync(session, ManifestHandle.AsPinnedId(u.Id), ctx, ct));
                var created = await createUser.Handle(createCmd, ct);
                EnsureOk(created, ctx);
                uid = ShortGuid.TryParse(created.Value.Id, out Guid cid) ? cid : null;
            }
            // A user with a pending deletion (admin recycle bin OR self-service grace) is
            // READ-ONLY: UpdateUserHandler rejects every edit until the user is restored.
            // Such a user still has IsDeleted=false, so the exporter lists them and every
            // later manifest carries them along — without this skip a single binned user
            // would fail EVERY subsequent apply (the whole transaction rolls back), which
            // also blocks deleting any OTHER user, because staged deletes apply through
            // this same path. Leave the lifecycle state alone: the way back is a restore,
            // then the next apply updates the profile again.
            else if (await session.LoadAsync<UserDeletionState>(existing.Id, ct) is { IsDeletionPending: true })
            {
                logger.LogInformation(
                    "Manifest apply skipped {Context}: the user has a pending deletion and is read-only. "
                    + "Restore the user (or let the retention purge finish) to make it writable again.", ctx);
                uid = existing.Id;
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

                // Active state is declarative here exactly as it is for service accounts
                // and positions. Deactivating is a kill switch, but its cascade is
                // DEFERRED (DeferringUserAccessRevoker) until the apply commits — which
                // is the whole reason this can live in a manifest at all.
                if (u.IsActive is { } wantActive)
                    await SetUserActiveAsync(session, sp, existing.Id, wantActive, ct);
            }
            if (uid.HasValue)
            {
                identity.Assign(u.Id, uid.Value);
                identity.Applied(ManifestIdentity.Sections.Users, uid.Value);
            }
        }

        // ── Groups (identity = Id; the Name is mutable) ───────────────────────────
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
                // ADR 0024: the Id names the group, so an id-matched entry RENAMES it (this
                // is what makes a rename survive an export → apply round trip). Without an
                // id the entry creates, and a taken name fails with Group.NameTaken —
                // never a silent takeover of a same-named group that means something else.
                var existing = await MatchByPinnedIdAsync<Group>(session, g.Id, x => x.IsDeleted, ct);

                // v2 merge-patch: absent lists/fields keep the existing group's values
                // (the canonical update is a full payload replace, so merge here).
                // Members/roles name entities by id, or by a handle this same file declared.
                List<Guid> memberIds;
                if (g.Members is null && existing is not null)
                {
                    memberIds = existing.MemberIds.ToList();
                }
                else
                {
                    memberIds = new List<Guid>((g.Members ?? []).Count);
                    foreach (var m in g.Members ?? [])
                    {
                        if (await ResolvePrincipalRefAsync(
                                session, identity, m, $"{ctx} member '{m}'", skips, membersOnly: true, ct) is { } uid)
                            memberIds.Add(uid);
                    }
                    // Every listed member was unresolvable → keep the stored membership
                    // instead of emptying it (a replace-list of nothing is not an intent).
                    memberIds = OrUnchangedWhenNothingResolved(
                        memberIds, (g.Members ?? []).Count, ctx, "member", skips)
                        ?? existing?.MemberIds.ToList() ?? [];
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
                    {
                        if (await ResolveRoleRefAsync(session, identity, rk, $"{ctx} role '{rk}'", skips, ct) is { } rid)
                            groupRoleIds.Add(rid);
                    }
                    groupRoleIds = OrUnchangedWhenNothingResolved(
                        groupRoleIds, (g.Roles ?? []).Count, ctx, "role", skips)
                        ?? existing?.RoleIds.ToList() ?? [];
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
                        session, ManifestHandle.AsPinnedId(g.Id), "Group", ctx, x => x.IsDeleted, ct);
                    // Create-branch mirrors the create endpoint's BoundTo default (see import).
                    var createdGroup = await createHandler.Handle(new CreateGroupCommand(
                        g.Name, description, memberIds, groupRoleIds, mode,
                        script, email, emailMode,
                        g.BoundTo ?? [AppSlugs.Modgud], externallyDrivable, CallerIsRealmAdmin: true,
                        Id: pinnedGroup.Id, ReviveExistingStream: pinnedGroup.Revive), ct);
                    EnsureOk(createdGroup, ctx);
                    identity.Assign(g.Id, createdGroup.Value.Id);
                    identity.Applied(ManifestIdentity.Sections.Groups, createdGroup.Value.Id);
                }
                else
                {
                    identity.Assign(g.Id, existing.Id);
                    identity.Applied(ManifestIdentity.Sections.Groups, existing.Id);
                    EnsureOk(await updateHandler.Handle(new UpdateGroupCommand(
                        existing.Id, g.Name, description, memberIds, groupRoleIds, mode,
                        script, email, emailMode,
                        g.BoundTo, externallyDrivable, CallerIsRealmAdmin: true), ct), ctx);
                }
            }
        }

        // ── Service accounts (hulls, id-pinned creates) ───────────────────────────
        await ApplyServiceAccountsAsync(sp, manifest, identity, apps, secrets, skips, ct);

        // ── Positions (MG-FT) — after users so grants can resolve their handles ───
        await ApplyPositionsAsync(sp, manifest, identity, skips, ct);

        // ── Prune / staged deletions: removal of entities absent from the manifest. Runs
        //    AFTER the upsert so the protection checks see the desired (post-merge) role
        //    graph. Prune sweeps everything; staged deletions target only their keys.
        if (prune || deletions is { Count: > 0 })
            await PruneAsync(sp, session, identity, appAdmin, oauth, roleAdmin, prune,
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
    /// <para>"Absent from the manifest" is read by IDENTITY (ADR 0024): the sweep runs AFTER
    /// the upsert and keeps exactly the entity ids that upsert touched. Keeping by name would
    /// mean a manifest entry that renamed an entity no longer matches the live one, which
    /// would prune the very entity the apply just wrote.</para>
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
        IServiceProvider sp, IDocumentSession session,
        ManifestIdentity identity, AppAdminService appAdmin, OAuthAdminService oauth,
        RoleAdminService roleAdmin, bool prune,
        IReadOnlyDictionary<string, HashSet<string>>? targeted,
        CancellationToken ct)
    {
        var perms = sp.GetRequiredService<IPermissionService>();

        // Targeted (staged) deletions restrict the sweep to their keys; a full prune
        // deletes every candidate. Everything else — keep-sets, infra/lockout guards,
        // canonical delete ops, ordering — is byte-identical for both modes.
        //
        // The keys are what the admin picked off a LIVE list ("delete this row"), which is
        // a different question from "which entity does this manifest entry mean" — so they
        // stay names while identity (below) is the id.
        bool Wants(string section, string key)
            => prune || (targeted?.TryGetValue(section, out var keys) == true && keys.Contains(key));

        // "Represented in the manifest" is an IDENTITY question (ADR 0024), so the keep-set
        // is what the apply just created or updated, by id — not what shares a name with a
        // manifest entry. That also closes the old hole where an entry that RENAMED an
        // entity left the live one (still carrying its old name) looking prunable.
        bool Keep(string section, Guid id) => identity.WasApplied(section, id);

        // ── Positions — first: pruning a position cascades its terminal slots + their
        //    terminal-managed clients (see the Positions partial). ─────────────────────────
        await PrunePositionsAsync(sp, session, oauth, identity, prune, targeted, ct);

        // ── Clients — keep SA-linked and terminal-managed clients (both are auto-managed
        //    credential material the manifest doesn't model). ───────────────────────────────
        foreach (var c in await session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.Clients, c.Id)
                || c.LinkedPositionPrincipalId.HasValue
                || !Wants("clients", c.ClientId)) continue;
            // A service-account credential is only prunable when the manifest actually
            // speaks for its account. An account the file never mentions keeps every
            // credential it has — the alternative is that omitting an account silently
            // cuts off whatever authenticates as it.
            if (c.LinkedServiceAccountId is { } ownerId)
            {
                if (!Keep(ManifestIdentity.Sections.ServiceAccounts, ownerId)) continue;
                // …and it goes through the SA-scoped delete: /admin/oauth/clients refuses
                // to mutate an SA-owned client at all, which is the guard that keeps a
                // credential's lifecycle attached to its account.
                EnsureOk(await oauth.DeleteServiceAccountCredentialAsync(ownerId, c.Id.ToString(), ct),
                    $"prune service-account credential '{c.ClientId}'");
                continue;
            }
            EnsureOk(await oauth.DeleteClientAsync(c.Id.ToString(), ct), $"prune client '{c.ClientId}'");
        }

        // ── Login providers — keep the built-in Internal provider. ───────────────────────────
        var deleteProvider = new DeleteLoginProviderHandler(session, sp.GetRequiredService<TimeProvider>());
        foreach (var p in await session.Query<LoginProvider>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.LoginProviders, p.Id) || p.IsBuiltIn
                || !Wants("loginProviders", p.Slug)) continue;
            EnsureOk(await deleteProvider.Handle(new DeleteLoginProviderCommand(p.Id), ct),
                $"prune login provider '{p.Slug}'");
        }

        // ── Scopes — keep auto-seeded standard scopes. ───────────────────────────────────────
        foreach (var s in await session.Query<OAuthScopeState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.Scopes, s.Id) || StandardScopes.IsStandard(s.Name)
                || !Wants("scopes", s.Name)) continue;
            EnsureOk(await oauth.DeleteScopeAsync(s.Id.ToString(), ct), $"prune scope '{s.Name}'");
        }

        // ── APIs. ────────────────────────────────────────────────────────────────────────────
        foreach (var a in await session.Query<OAuthApiState>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.Apis, a.Id) || !Wants("apis", a.Name)) continue;
            EnsureOk(await oauth.DeleteApiAsync(a.Id.ToString(), ct), $"prune api '{a.Name}'");
        }

        // ── Groups — keep admin-conferring groups (lockout guard). ───────────────────────────
        var groupHandler = new DeleteGroupHandler(session);
        foreach (var g in await session.Query<Group>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.Groups, g.Id) || !Wants("groups", g.Name)) continue;
            if (await GroupMembershipGuards.GroupConfersRealmAdminAsync(session, perms, g, ct)) continue;
            EnsureOk(await groupHandler.Handle(new DeleteGroupCommand(g.Id), ct), $"prune group '{g.Name}'");
        }

        // ── Users — keep anyone who holds realm:admin. ───────────────────────────────────────
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
            if (Keep(ManifestIdentity.Sections.Users, p.Id) || !WantsUser(p)) continue;
            if (await perms.HasPermissionAsync(p.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct))
                continue;
            EnsureOk(await userHandler.Handle(new DeleteUsersCommand([p.Id]), ct), $"prune user '{p.AccountName ?? p.Id.ToString()}'");
        }

        // ── Roles — keep realm-admin roles (lockout guard). ──────────────────────────────────
        var roleAppSlugById = (await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct))
            .ToDictionary(a => a.Id, a => a.Slug);
        foreach (var r in await session.Query<PermissionRole>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            var roleKey = RoleKeys.Qualified(
                !r.IsRealmAdmin && r.AppId is { } aid ? roleAppSlugById.GetValueOrDefault(aid) : null, r.Name);
            if (Keep(ManifestIdentity.Sections.Roles, r.Id) || r.IsRealmAdmin
                || !Wants("roles", roleKey)) continue;
            EnsureOk(await roleAdmin.DeleteRoleAsync(r.Id, ct), $"prune role '{roleKey}'");
        }

        // ── Apps — keep the system app; a still-referenced app errors. ───────────────────────
        foreach (var a in await session.Query<App>().Where(x => !x.IsDeleted).ToListAsync(ct))
        {
            if (Keep(ManifestIdentity.Sections.Apps, a.Id) || a.IsSystem
                || !Wants("apps", a.Slug)) continue;
            EnsureOk(await appAdmin.DeleteAppAsync(a.Id, ct), $"prune app '{a.Slug}'");
        }
    }

    /// <summary>
    /// Manifest client → canonical create DTO. Every optional field falls back to the SAME
    /// shipped default the admin API applies on create, so the manifest path and the manual
    /// path can never diverge (guarded by RealmManifestParityTests).
    /// </summary>
    private static CreateOAuthClientDto BuildClientCreateDto(
        RealmManifestClient c, IReadOnlyDictionary<string, App> apps, string ctx,
        ManifestReferenceSkips skips) => new()
    {
        Id = ManifestHandle.AsPinnedId(c.Id),
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
        JsonWebKeySet = OrNull(c.JsonWebKeySet),
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
        // A client's app link is optional, so an app this realm does not have simply
        // drops out; an all-unresolvable list leaves the link alone rather than clearing it.
        AppIds = c.Apps is not { Count: > 0 }
            ? null
            : OrUnchangedWhenNothingResolved(
                c.Apps.Select(slug => ResolveAppId(apps, slug, ctx, skips)).OfType<string>().ToList(),
                c.Apps.Count, ctx, "app", skips),
    };

    /// <summary>
    /// Manifest client → canonical update DTO. v2 merge-patch is a straight
    /// pass-through: the update DTO shares the manifest's semantics — absent
    /// Optionals/lists stay unchanged, explicit null clears, [] clears a list.
    /// </summary>
    private static UpdateOAuthClientDto BuildClientUpdateDto(
        RealmManifestClient c, IReadOnlyDictionary<string, App> apps, string ctx,
        ManifestReferenceSkips skips) => new()
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
        JsonWebKeySet = c.JsonWebKeySet,
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
        AppIds = c.Apps is null
            ? null
            : OrUnchangedWhenNothingResolved(
                c.Apps.Select(slug => ResolveAppId(apps, slug, ctx, skips)).OfType<string>().ToList(),
                c.Apps.Count, ctx, "app", skips),
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

    /// <summary>
    /// Resolves a user reference (ADR 0024). A <c>#handle</c> names a user this same
    /// manifest creates and always resolves — the validation pass already refused a handle
    /// nothing declares. A real id resolves against this realm; a realm that has no such
    /// user makes it a reported SKIP. A <c>Key</c> beside the id is a verified hint: never
    /// followed, but reported when it disagrees with the user the id names.
    /// </summary>
    private static async Task<Guid?> ResolveUserRefAsync(
        IDocumentSession session, ManifestIdentity identity, ManifestRef reference,
        string context, ManifestReferenceSkips skips, CancellationToken ct)
        => await ResolvePrincipalRefAsync(session, identity, reference, context, skips, membersOnly: false, ct);

    /// <summary>
    /// Resolves a reference to a PRINCIPAL. A group's members may be users, nested groups
    /// (the domain really expands them — permissions via ApplicationScopeResolver, mail via
    /// Group.GetEmailsAsync) or service accounts, so the manifest has to be able to name all
    /// three; filtering the others out is what made export → apply DELETE them, Members
    /// being a replace-list. A position grant is narrower — only a person can staff a shift —
    /// so <paramref name="membersOnly"/> keeps that distinction where it belongs.
    ///
    /// <para>The in-run check comes before the document load in each case: an entity this
    /// apply just created may not be readable from the projection yet.</para>
    /// </summary>
    private static async Task<Guid?> ResolvePrincipalRefAsync(
        IDocumentSession session, ManifestIdentity identity, ManifestRef reference,
        string context, ManifestReferenceSkips skips, bool membersOnly, CancellationToken ct)
    {
        if (reference.Handle is { } handle)
            return ResolveHandle(identity, handle, membersOnly ? "member" : "user", context, skips);

        if (reference.ParsedId is not { } byId)
        {
            // Unreachable after ManifestIdentity.Validate — kept so a future caller that
            // skips the validation fails loudly instead of silently dropping the reference.
            skips.Skip(context, "user", "carries no id or #handle, and a name is never resolved (ADR 0024)");
            return null;
        }

        if (identity.WasApplied(ManifestIdentity.Sections.Users, byId)) return byId;
        if (await session.LoadAsync<Person>(byId, ct) is { IsDeleted: false } person)
        {
            VerifyHint(reference, context, "user", skips, person.AccountName, person.Email);
            return person.Id;
        }

        if (!membersOnly)
        {
            skips.Skip(context, $"user '{reference.Display}'", "no user with that id in this realm");
            return null;
        }

        if (identity.WasApplied(ManifestIdentity.Sections.Groups, byId)
            || identity.WasApplied(ManifestIdentity.Sections.ServiceAccounts, byId)) return byId;
        if (await session.LoadAsync<Group>(byId, ct) is { IsDeleted: false } group)
        {
            VerifyHint(reference, context, "group", skips, group.Name);
            return group.Id;
        }
        if (await session.LoadAsync<ServiceAccount>(byId, ct) is { IsDeleted: false } sa)
        {
            VerifyHint(reference, context, "service account", skips, sa.AccountName);
            return sa.Id;
        }
        skips.Skip(context, $"member '{reference.Display}'",
            "no user, group or service account with that id in this realm");
        return null;
    }

    /// <summary>
    /// Resolves a group's role reference (ADR 0024) — same three forms as a user reference:
    /// a <c>#handle</c> from this manifest, a real id against this realm (missing = reported
    /// skip), and a <c>Key</c> that is only a verified hint.
    /// </summary>
    private static async Task<Guid?> ResolveRoleRefAsync(
        IDocumentSession session, ManifestIdentity identity, ManifestRef reference,
        string context, ManifestReferenceSkips skips, CancellationToken ct)
    {
        if (reference.Handle is { } handle)
            return ResolveHandle(identity, handle, "role", context, skips);

        if (reference.ParsedId is not { } byId)
        {
            skips.Skip(context, "role", "carries no id or #handle, and a name is never resolved (ADR 0024)");
            return null;
        }
        if (identity.WasApplied(ManifestIdentity.Sections.Roles, byId)) return byId;
        var role = await session.LoadAsync<PermissionRole>(byId, ct);
        if (role is null || role.IsDeleted)
        {
            skips.Skip(context, $"role '{reference.Display}'", "no role with that id in this realm");
            return null;
        }
        VerifyHint(reference, context, "role", skips, role.Name);
        return role.Id;
    }

    /// <summary>
    /// Mirrors the canonical active-state write (<c>V2_User_Update</c>): flip the flag,
    /// append the lifecycle event, and run the kill switch ONLY on a real active → inactive
    /// transition, so a re-applied manifest does not churn the security stamp. Inside an
    /// apply the revoker is the deferring decorator, so the revocation happens after the
    /// commit — and not at all if the apply rolls back.
    /// </summary>
    private static async Task SetUserActiveAsync(
        IDocumentSession session, IServiceProvider sp, Guid userId, bool wanted, CancellationToken ct)
    {
        var appUser = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (appUser is null) return;
        var wasActive = appUser.IsActive;
        if (wasActive == wanted) return;

        appUser.IsActive = wanted;
        session.Store(appUser);
        session.Events.Append(userId, wanted
            ? new UserActivatedEvent(userId)
            : (object)new UserDeactivatedEvent(userId));
        await session.SaveChangesAsync(ct);

        if (!wanted)
            await sp.GetRequiredService<IUserAccessRevoker>()
                .RevokeAllAccessAsync(userId, AccessRevocationReason.Deactivation, ct);
    }

    /// <summary>
    /// A declared handle should always resolve — the validation pass rejects one that is
    /// undeclared, of the wrong kind, or declared in a section applied later. It can still
    /// come back empty for a reason validation cannot see: the entry that declares it was
    /// itself SKIPPED at apply time (a role whose app this realm does not have). That is a
    /// legitimate outcome, but not a silent one — the reference has to say it dropped, or
    /// the group simply comes out with fewer roles and the response claims success.
    /// </summary>
    private static Guid? ResolveHandle(
        ManifestIdentity identity, string handle, string what, string context, ManifestReferenceSkips skips)
    {
        if (identity.Resolve(handle) is { } id) return id;
        skips.Skip(context, $"{what} '{handle}'",
            "the entry declaring this handle was itself skipped, so there is no entity to point at");
        return null;
    }

    /// <summary>
    /// A <c>Key</c> next to a real id is documentation, not identity: the apply follows the
    /// id and never the name. But a name that has gone stale still misleads whoever reads
    /// the file next, so a disagreement is reported — the one thing a silent resolution
    /// cannot do. The live key is matched loosely (the reference may spell a role as
    /// <c>app/name</c> or a user as either username or email), so only a genuine mismatch
    /// is called out.
    /// </summary>
    private static void VerifyHint(
        ManifestRef reference, string context, string what, ManifestReferenceSkips skips,
        params string?[] liveKeys)
    {
        if (reference.Key is not { Length: > 0 } hint) return;
        var live = liveKeys.Where(k => !string.IsNullOrEmpty(k)).ToList();
        if (live.Count == 0) return;
        var (_, bare) = RoleKeys.Split(hint);
        if (live.Any(k => hint.Equals(k, StringComparison.OrdinalIgnoreCase)
                          || bare.Equals(k, StringComparison.OrdinalIgnoreCase))) return;
        skips.Note(context,
            $"the {what} named by this id is '{live[0]}', not '{hint}' — the id was followed "
            + "(identity is the id); update the Key so the file reads true.");
    }

    /// <summary>An app reference: resolves to the app's id, or to null when this realm has no
    /// such app. A missing app is SKIPPED, not an error — the app link is an optional
    /// grouping (a client with no app is a legal, ordinary client), and the applier seeds its
    /// resolver with every app already in the realm, so a file that omits an app the target
    /// already has resolves fine. See <see cref="ManifestReferenceSkips"/>.</summary>
    private static string? ResolveAppId(
        IReadOnlyDictionary<string, App> apps, string? slug, string context, ManifestReferenceSkips skips)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        if (!apps.TryGetValue(slug, out var app))
        {
            skips.Skip(context, $"app '{slug}'", "no such app in this realm");
            return null;
        }
        return new ShortGuid(app.Id).ToString();
    }

    /// <summary>
    /// Resolves <c>resource:action</c> permission keys against the catalog of
    /// <paramref name="appSlug"/>. Permissions the catalog does not carry are skipped, the
    /// rest resolve.
    ///
    /// <para>Returns <c>null</c> — meaning "leave the stored value alone" — when a non-empty
    /// permission list yields nothing, which happens either because the app itself is absent
    /// (no catalog to look in at all) or because none of the listed permissions are in it.
    /// Writing <c>[]</c> there would CLEAR an existing role's permissions on the strength of
    /// a lookup that never happened; rule 2 in <see cref="ManifestReferenceSkips"/>.</para>
    /// </summary>
    private static List<string>? ResolvePermissionIds(
        IReadOnlyDictionary<string, App> apps, string? appSlug, List<RealmManifestPermission>? perms,
        string context, ManifestReferenceSkips skips)
    {
        if (perms is not { Count: > 0 }) return [];

        if (string.IsNullOrEmpty(appSlug) || !apps.TryGetValue(appSlug, out var app))
        {
            skips.SkipWholeList(context, "permission", perms.Count);
            return null;
        }

        var catalog = app.Permissions.ToDictionary(p => $"{p.Resource}:{p.Action}", p => p.Id);
        var ids = new List<string>(perms.Count);
        foreach (var p in perms)
        {
            if (catalog.TryGetValue($"{p.Resource}:{p.Action}", out var pid))
                ids.Add(new ShortGuid(pid).ToString());
            else
                skips.Skip(context, $"permission '{p.Resource}:{p.Action}'",
                    $"not in the catalog of app '{appSlug}'");
        }

        if (ids.Count == 0)
        {
            skips.SkipWholeList(context, "permission", perms.Count);
            return null;
        }
        return ids;
    }

    /// <summary>Applies rule 2 to a resolved reference list: a non-empty input that resolved
    /// to nothing becomes <c>null</c> (unchanged) instead of <c>[]</c> (cleared).</summary>
    private static List<T>? OrUnchangedWhenNothingResolved<T>(
        List<T> resolved, int listed, string context, string field, ManifestReferenceSkips skips)
    {
        if (listed == 0 || resolved.Count > 0) return resolved;
        skips.SkipWholeList(context, field, listed);
        return null;
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
    /// Entity matching, and the ONLY form of it (ADR 0024): a manifest entity whose
    /// <c>Id</c> resolves to a LIVE document of its own type IS that entity — the apply
    /// updates it to the manifest's values, INCLUDING its natural key, because the id is
    /// the identity and the key is mutable metadata.
    ///
    /// <para>Returns null — meaning "this entry CREATES" — when the entity carries no id,
    /// carries a <c>#handle</c> (a name for something this file is about to create), or
    /// carries an id this realm does not have: free (the create pins it, which is what
    /// keeps ids identical across environments) or owned by a soft-deleted entity of the
    /// same type (the create revives it). A name is never consulted, so a create whose
    /// natural key is already taken fails loudly instead of overwriting a stranger.</para>
    /// </summary>
    private static async Task<TDoc?> MatchByPinnedIdAsync<TDoc>(
        IDocumentSession session, string? raw, Func<TDoc, bool> isDeleted, CancellationToken ct)
        where TDoc : class
    {
        if (ManifestHandle.AsPinnedId(raw) is not { } pinned
            || !ShortGuid.TryParse(pinned, out Guid id)) return null;
        var doc = await session.LoadAsync<TDoc>(id, ct);
        return doc is not null && !isDeleted(doc) ? doc : null;
    }

    /// <summary>
    /// Re-attaches the per-App settings that a manifest deliberately never carries: the
    /// branding asset ids, the login-provider allow-list and the default self-registration
    /// groups (see <c>RealmManifestExporter.WithoutRealmLocalReferences</c>).
    ///
    /// <para>Necessary because a per-App settings section is REPLACE, not merge-patch:
    /// <c>ApplicationSettingsService.StageNonOriginAsync</c> rebuilds a whole section from
    /// the DTO whenever the section is present, so a section that arrives with those fields
    /// nulled does not leave them alone — it CLEARS them. Re-applying an unedited export
    /// would have wiped an App's logo and, worse, turned an allow-list of one login provider
    /// into "every enabled provider".</para>
    ///
    /// <para>Reading them back from the stored override restores the intent the transport
    /// drops: the manifest cannot carry these, therefore the manifest never changes them.
    /// On a realm that has no override yet there is nothing to restore, so nothing dangles.</para>
    /// </summary>
    private static async Task<ApplicationSettingsDto?> KeepRealmLocalSettingsAsync(
        IServiceProvider sp, Guid appId, ApplicationSettingsDto? incoming, CancellationToken ct)
    {
        if (incoming is null) return null;
        var stored = await sp.GetRequiredService<IApplicationSettingsService>().GetAsync(appId, ct);
        if (stored.IsError) return incoming;

        return incoming with
        {
            Branding = incoming.Branding is null ? null : incoming.Branding with
            {
                LogoAssetId = stored.Value.Branding?.LogoAssetId,
                FaviconAssetId = stored.Value.Branding?.FaviconAssetId,
            },
            LoginExperience = incoming.LoginExperience is null ? null : incoming.LoginExperience with
            {
                LoginProviderIds = stored.Value.LoginExperience?.LoginProviderIds,
            },
            SelfRegistration = incoming.SelfRegistration is null ? null : incoming.SelfRegistration with
            {
                DefaultGroupIds = stored.Value.SelfRegistration?.DefaultGroupIds,
            },
        };
    }

    /// <summary>Binds a handle to the id a create actually produced and records the entity
    /// as touched (prune's keep-set). The id comes back from the canonical op as a string,
    /// so an unparseable one is a contract break worth failing on rather than dropping.</summary>
    private static void RegisterApplied(
        ManifestIdentity identity, string section, string? manifestId, string createdId, string ctx)
    {
        if (!ShortGuid.TryParse(createdId, out Guid id))
            throw new ManifestApplyException(ctx, [Error.Unexpected("Manifest.UnreadableId",
                $"{ctx}: the create returned '{createdId}', which is not a valid id.")]);
        identity.Assign(manifestId, id);
        identity.Applied(section, id);
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
