using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Produces a <see cref="RealmManifest"/> from a realm's CURRENT state — the inverse of
/// <see cref="RealmManifestApplier"/>. The export is STRUCTURE-ONLY: it never emits client
/// secrets or user passwords (those are stored as one-way hashes and can't be recovered).
/// Re-applying the export with <c>POST /{slug}/apply</c> is therefore a no-op on credentials
/// (confidential clients keep their secret; users keep their password) — set a fresh password
/// by adding it to a user before re-applying.
///
/// <para>Cross-references are reversed back to KEYS (app slug, role/user key,
/// <c>resource:action</c>). Entities that can't be cleanly re-applied are omitted: the
/// auto-seeded standard OIDC scopes and system apps, plus service-account-linked clients (the
/// manifest doesn't model service accounts). Realm settings ARE exported (all sections, current
/// values) EXCEPT the write-only captcha secret (a <c>CaptchaSecretSet</c> flag, never the
/// plaintext) — re-applying leaves that untouched.</para>
/// </summary>
public sealed class RealmManifestExporter(
    IRealmProvisioningService realms,
    IServiceScopeFactory scopeFactory)
{
    // OpenIddict's scope-permission prefix; a client's requested scopes are stored as
    // "scp:<name>" entries in its permission list.
    private const string ScopePrefix = "scp:";

    public async Task<ErrorOr<RealmManifest>> ExportRealmAsync(string slug, CancellationToken ct = default)
    {
        var realm = await realms.GetRealmBySlugAsync(slug, ct);
        if (realm is null)
            return Error.NotFound("Realm.NotFound", $"Realm '{slug}' does not exist.");

        using var _ = TenantContext.Enter(slug);
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<IDocumentSession>();
        var oauth = sp.GetRequiredService<OAuthAdminService>();

        // Realm settings (all sections, current values) reverse-mapped read→patch shape.
        var settings = MapSettings(await sp.GetRequiredService<IRealmSettingsService>().GetDtoAsync(ct));

        // ── Apps + reverse-resolution maps (these cover ALL apps incl. system, so
        //    downstream references to a system app still resolve to a slug). ──────────
        var apps = await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct);
        var appSlugById = apps.ToDictionary(a => a.Id, a => a.Slug);
        var permKeyById = new Dictionary<Guid, RealmManifestPermission>();
        foreach (var a in apps)
            foreach (var p in a.Permissions)
                permKeyById[p.Id] = new RealmManifestPermission(p.Resource, p.Action, p.Description);

        // System apps are auto-seeded — not part of a realm's authored config.
        var manifestApps = apps.Where(a => !a.IsSystem).Select(a => new RealmManifestApp
        {
            Slug = a.Slug,
            DisplayName = a.DisplayName,
            Description = a.Description,
            Permissions = a.Permissions
                .Select(p => new RealmManifestPermission(p.Resource, p.Action, p.Description)).ToList(),
        }).ToList();

        // ── APIs / scopes / clients via the admin DTOs (flags already resolved) ──────
        var apis = (await oauth.GetApisAsync(new PaginationRequest { PageSize = 1000 }, ct)).Items;
        var manifestApis = apis.Select(api => new RealmManifestApi
        {
            Name = api.Name,
            DisplayName = api.DisplayName,
            Description = api.Description,
            App = SlugOfShort(appSlugById, api.AppId),
            Scopes = api.Scopes,
            UserClaims = api.UserClaims,
            Permissions = PermsOfShort(permKeyById, api.PermissionIds),
            Enabled = api.Enabled,
            AllowDynamicRegistration = api.AllowDynamicRegistration,
        }).ToList();

        // Standard OIDC scopes are auto-seeded and rejected by the update path — omit them.
        var scopes = (await oauth.GetScopesAsync(ct)).Items.Where(s => !s.IsStandard);
        var manifestScopes = scopes.Select(s => new RealmManifestScope
        {
            Name = s.Name,
            DisplayName = s.DisplayName,
            Description = s.Description,
            App = SlugOfShort(appSlugById, s.AppId),
            Resources = s.Resources,
            UserClaims = s.UserClaims,
            Enabled = s.Enabled,
            Required = s.Required,
            Emphasize = s.Emphasize,
            ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
        }).ToList();

        // Service-account-linked clients are M2M credentials the manifest can't model — skip.
        var clients = (await oauth.GetClientsAsync(new PaginationRequest { PageSize = 1000 }, ct))
            .Items.Where(c => c.LinkedServiceAccountId is null);
        var manifestClients = clients.Select(c => new RealmManifestClient
        {
            ClientId = c.ClientId,
            DisplayName = c.DisplayName,
            ClientType = c.ClientType,
            // No ClientSecret — it's a hash; a re-import generates a fresh one.
            RedirectUris = c.RedirectUris,
            PostLogoutRedirectUris = c.PostLogoutRedirectUris,
            Scopes = c.Permissions.Where(p => p.StartsWith(ScopePrefix, StringComparison.Ordinal))
                .Select(p => p[ScopePrefix.Length..]).ToList(),
            AllowedGrantTypes = c.AllowedGrantTypes,
            Apps = c.AppIds.Select(id => SlugOfShort(appSlugById, id)).Where(s => s is not null).Select(s => s!).ToList(),
            Roles = c.Roles,
            WebAuthnRpId = c.WebAuthnRpId,
            Enabled = c.Enabled,
            RequireConsent = c.RequireConsent,
        }).ToList();

        // ── Roles (raw — ids are Guids) ──────────────────────────────────────────────
        var roles = await session.Query<PermissionRole>().Where(r => !r.IsDeleted).ToListAsync(ct);
        var roleKeyById = roles.ToDictionary(r => r.Id, r => r.Name);
        var manifestRoles = roles.Select(r => new RealmManifestRole
        {
            Name = r.Name,
            Description = r.Description,
            App = r.AppId is { } aid && appSlugById.TryGetValue(aid, out var slugOf) ? slugOf : null,
            IsRealmAdmin = r.IsRealmAdmin,
            Permissions = r.PermissionIds
                .Where(permKeyById.ContainsKey).Select(id => permKeyById[id]).ToList(),
        }).ToList();

        // ── Users (raw Person for the human list + ApplicationUser for EmailConfirmed) ─
        var persons = await session.Query<Person>().Where(p => !p.IsDeleted).ToListAsync(ct);
        var appUsers = (await session.Query<ApplicationUser>().ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u);
        var userKeyById = persons.ToDictionary(p => p.Id, p => p.AccountName ?? p.Email ?? p.Id.ToString());
        var manifestUsers = persons.Select(p => new RealmManifestUser
        {
            Key = p.AccountName ?? p.Email,
            Firstname = p.Firstname,
            Lastname = p.Lastname,
            Acronym = p.Acronym,
            Email = p.Email ?? string.Empty,
            UserName = p.AccountName,
            // No Password — stored as a hash. Add one before re-applying to set it.
            EmailConfirmed = appUsers.TryGetValue(p.Id, out var au) && au.EmailConfirmed,
        }).ToList();

        // ── Groups (raw — ids are Guids; resolve members→user keys, roles→role names) ─
        var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
        var manifestGroups = groups.Select(g => new RealmManifestGroup
        {
            Name = g.Name,
            Description = g.Description,
            Members = g.MemberIds.Where(userKeyById.ContainsKey).Select(id => userKeyById[id]).ToList(),
            Roles = g.RoleIds.Where(roleKeyById.ContainsKey).Select(id => roleKeyById[id]).ToList(),
            MembershipMode = g.MembershipMode.ToString(),
            MembershipScript = g.MembershipScript,
            Email = g.Email,
            EmailMode = g.EmailMode.ToString(),
            BoundTo = g.BoundTo.Count == 0 ? null : g.BoundTo,
            ExternallyDrivable = g.ExternallyDrivable,
        }).ToList();

        return new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = realm.Slug,
                DisplayName = realm.DisplayName,
                Description = realm.Description,
                Domains = realm.Domains,
                PrimaryDomain = realm.PrimaryDomain,
                // InitialAdmin is meaningless for an existing realm; left default (ignored on apply).
            },
            Settings = settings,
            Apps = manifestApps,
            Apis = manifestApis,
            Scopes = manifestScopes,
            Clients = manifestClients,
            Roles = manifestRoles,
            Users = manifestUsers,
            Groups = manifestGroups,
        };
    }

    /// <summary>
    /// Reverse-maps the realm-settings read shape to the patch shape the manifest carries —
    /// every section emitted with its current effective values so the export shows the full
    /// config. The write-only captcha secret is intentionally left null (no plaintext to read);
    /// re-applying leaves the stored secret untouched.
    /// </summary>
    private static UpdateRealmSettingsDto MapSettings(RealmSettingsDto s) => new()
    {
        SelfRegistration = new UpdateSelfRegistrationDto
        {
            Enabled = s.SelfRegistration.Enabled,
            RequireEmailVerification = s.SelfRegistration.RequireEmailVerification,
            AllowedEmailDomains = s.SelfRegistration.AllowedEmailDomains,
            RequireAdminApproval = s.SelfRegistration.RequireAdminApproval,
            DefaultGroupIds = s.SelfRegistration.DefaultGroupIds,
            TermsOfServiceUrl = s.SelfRegistration.TermsOfServiceUrl,
            PrivacyPolicyUrl = s.SelfRegistration.PrivacyPolicyUrl,
            CaptchaEnabled = s.SelfRegistration.CaptchaEnabled,
            CaptchaSiteKey = s.SelfRegistration.CaptchaSiteKey,
            // CaptchaSecret is write-only (only a CaptchaSecretSet flag is readable) — leave null.
        },
        Dcr = new UpdateDcrSettingsDto
        {
            Enabled = s.Dcr.Enabled,
            AccessTokenLifetimeMinutes = s.Dcr.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays = s.Dcr.RefreshTokenLifetimeDays,
            GcTtlDays = s.Dcr.GcTtlDays,
            PerIpRateLimitPerHour = s.Dcr.PerIpRateLimitPerHour,
            PerRealmRateLimitPerDay = s.Dcr.PerRealmRateLimitPerDay,
            ReservedNames = s.Dcr.ReservedNames,
        },
        Cimd = new UpdateCimdSettingsDto
        {
            Enabled = s.Cimd.Enabled,
            AccessTokenLifetimeMinutes = s.Cimd.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays = s.Cimd.RefreshTokenLifetimeDays,
        },
        NativeGrants = new UpdateNativeGrantSettingsDto
        {
            Enabled = s.NativeGrants.Enabled,
            AccessTokenLifetimeMinutes = s.NativeGrants.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays = s.NativeGrants.RefreshTokenLifetimeDays,
        },
        AuthRateLimits = new UpdateAuthRateLimitsDto
        {
            // Read + patch share RateLimitRuleDto, so the rules copy across directly.
            NativeOtp = s.AuthRateLimits.NativeOtp,
            MagicLink = s.AuthRateLimits.MagicLink,
            PasswordReset = s.AuthRateLimits.PasswordReset,
            EmailOtp = s.AuthRateLimits.EmailOtp,
            EmailVerification = s.AuthRateLimits.EmailVerification,
            PasskeyBegin = s.AuthRateLimits.PasskeyBegin,
            Bootstrap = s.AuthRateLimits.Bootstrap,
        },
        Branding = new UpdateBrandingSettingsDto
        {
            ProductName = s.Branding.ProductName,
            LogoAssetId = s.Branding.LogoAssetId,
            FaviconAssetId = s.Branding.FaviconAssetId,
            PrimaryColor = s.Branding.PrimaryColor,
        },
        RegistrationFields = new UpdateRegistrationFieldsSettingsDto
        {
            Username = s.RegistrationFields.Username,
            Firstname = s.RegistrationFields.Firstname,
            Lastname = s.RegistrationFields.Lastname,
        },
        Deletion = new UpdateDeletionSettingsDto
        {
            GraceDays = s.Deletion.GraceDays,
            ReminderLeadDays = s.Deletion.ReminderLeadDays,
            AdminRetentionDays = s.Deletion.AdminRetentionDays,
            AutoPurgeEnabled = s.Deletion.AutoPurgeEnabled,
        },
        Audit = new UpdateAuditSettingsDto
        {
            VisibilityWindowDays = s.Audit.VisibilityWindowDays,
        },
    };

    private static string? SlugOfShort(IReadOnlyDictionary<Guid, string> appSlugById, string? shortGuid)
        => !string.IsNullOrEmpty(shortGuid) && ShortGuid.TryParse(shortGuid, out Guid id)
            && appSlugById.TryGetValue(id, out var slug) ? slug : null;

    private static List<RealmManifestPermission> PermsOfShort(
        IReadOnlyDictionary<Guid, RealmManifestPermission> permKeyById, IEnumerable<string> shortGuids)
    {
        var result = new List<RealmManifestPermission>();
        foreach (var s in shortGuids)
            if (ShortGuid.TryParse(s, out Guid id) && permKeyById.TryGetValue(id, out var perm))
                result.Add(perm);
        return result;
    }
}
