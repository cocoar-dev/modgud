using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.DTOs.Applications;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Services;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Domain.Applications;
using Modgud.Domain.Common;
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
/// auto-seeded standard OIDC scopes and system apps, plus service-account-linked clients (they
/// travel under their account, see the ServiceAccounts section). Realm settings ARE exported
/// (all sections, current values) EXCEPT the write-only captcha secret (a
/// <c>CaptchaSecretSet</c> flag, never the plaintext) — which is "unchanged" under merge-patch,
/// so re-applying an export leaves it alone. Settings that name entities by raw id (default
/// groups, allowed login providers, branding assets) travel like every other id: the applier
/// skips and reports one the target realm does not have (ADR 0024).</para>
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
                permKeyById[p.Id] = new RealmManifestPermission(
                    p.Resource, p.Action, p.Description, new ShortGuid(p.Id).ToString());

        // System apps are auto-seeded — not part of a realm's authored config.
        // Settings: only apps that HAVE an override doc export one — an app without an
        // override inherits the realm everywhere, and exporting the (empty) effective DTO
        // would materialize a needless override on re-apply.
        var appSettingsSvc = sp.GetRequiredService<IApplicationSettingsService>();
        var manifestApps = new List<RealmManifestApp>();
        foreach (var a in apps.Where(a => !a.IsSystem))
        {
            ApplicationSettingsDto? appSettings = null;
            if (await session.LoadAsync<ApplicationSettings>(a.Id, ct) is not null)
            {
                var loaded = await appSettingsSvc.GetAsync(a.Id, ct);
                appSettings = loaded.IsError ? null : WithoutDerivedUrls(loaded.Value);
            }

            manifestApps.Add(new RealmManifestApp
            {
                Slug = a.Slug,
                Id = new ShortGuid(a.Id).ToString(),
                DisplayName = a.DisplayName,
                Description = Opt(a.Description),
                Permissions = a.Permissions
                    .Select(p => new RealmManifestPermission(
                        p.Resource, p.Action, p.Description, new ShortGuid(p.Id).ToString())).ToList(),
                Settings = appSettings,
            });
        }

        // ── APIs / scopes / clients via the admin DTOs (flags already resolved) ──────
        var apis = (await oauth.GetApisAsync(new PaginationRequest { PageSize = 1000 }, ct)).Items;
        var manifestApis = apis.Select(api => new RealmManifestApi
        {
            Name = api.Name,
            Id = PinId(api.Id),
            DisplayName = Opt(api.DisplayName),
            Description = Opt(api.Description),
            App = Opt(SlugOfShort(appSlugById, api.AppId)),
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
            Id = PinId(s.Id),
            DisplayName = Opt(s.DisplayName),
            Description = Opt(s.Description),
            App = Opt(SlugOfShort(appSlugById, s.AppId)),
            Resources = s.Resources,
            UserClaims = s.UserClaims,
            Enabled = s.Enabled,
            Required = s.Required,
            Emphasize = s.Emphasize,
            ShowInDiscoveryDocument = s.ShowInDiscoveryDocument,
            AllowDynamicRegistrationClients = s.AllowDynamicRegistrationClients,
        }).ToList();

        // Service-account-linked clients travel under their account (Credentials), and a
        // terminal-managed client travels under its position's slot (Positions[].Terminals):
        // both are managed through their owner, and the generic client update refuses them.
        // A V2 terminal client is linked to its ENROLLMENT only (the position link is the
        // legacy single-position form), so the enrollment link is the one to test.
        var clients = (await oauth.GetClientsAsync(new PaginationRequest { PageSize = 1000 }, ct))
            .Items.Where(c => c.LinkedServiceAccountId is null
                              && c.LinkedPositionPrincipalId is null
                              && c.ManagedTerminalEnrollmentId is null);
        var manifestClients = clients.Select(c => new RealmManifestClient
        {
            ClientId = c.ClientId,
            Id = PinId(c.Id),
            DisplayName = Opt(c.DisplayName),
            ClientType = c.ClientType,
            // No ClientSecret — it's a hash; a re-import generates a fresh one.
            ConsentType = c.ConsentType,
            RedirectUris = c.RedirectUris,
            PostLogoutRedirectUris = c.PostLogoutRedirectUris,
            Scopes = c.Permissions.Where(p => p.StartsWith(ScopePrefix, StringComparison.Ordinal))
                .Select(p => p[ScopePrefix.Length..]).ToList(),
            AllowedGrantTypes = c.AllowedGrantTypes,
            Capabilities = c.Capabilities.Count == 0 ? null : c.Capabilities,
            AllowedCorsOrigins = c.AllowedCorsOrigins,
            Apps = c.AppIds.Select(id => SlugOfShort(appSlugById, id)).Where(s => s is not null).Select(s => s!).ToList(),
            Roles = c.Roles,
            WebAuthnRpId = Opt(c.WebAuthnRpId),
            BackChannelLogoutUri = Opt(c.BackChannelLogoutUri),
            JsonWebKeySet = Opt(c.JsonWebKeySet),
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
            AccessTokenType = c.AccessTokenType.ToString(),
            IdentityTokenLifetime = Opt(c.IdentityTokenLifetime),
            AccessTokenLifetime = Opt(c.AccessTokenLifetime),
            AuthorizationCodeLifetime = Opt(c.AuthorizationCodeLifetime),
            SlidingRefreshTokenLifetime = Opt(c.SlidingRefreshTokenLifetime),
            ClientSessionIdleLifetime = Opt(c.ClientSessionIdleLifetime),
            ClientSessionAbsoluteLifetime = Opt(c.ClientSessionAbsoluteLifetime),
            Claims = c.Claims.Select(cl => new RealmManifestClientClaim(cl.Type, cl.Value)).ToList(),
            ClientClaimsPrefix = Opt(c.ClientClaimsPrefix),
            AlwaysSendClientClaims = c.AlwaysSendClientClaims,
            UpdateAccessTokenClaimsOnRefresh = c.UpdateAccessTokenClaimsOnRefresh,
        }).ToList();

        // ── Login providers — the seeded built-in Internal provider is infra, not authored
        //    config; secrets are DataProtection-encrypted and never exported (add one to a
        //    provider before re-applying to rotate it). ────────────────────────────────────
        var providers = await session.Query<LoginProvider>()
            .Where(p => !p.IsDeleted && !p.IsBuiltIn).ToListAsync(ct);
        var manifestProviders = providers.Select(p => new RealmManifestLoginProvider
        {
            Slug = p.Slug,
            Id = new ShortGuid(p.Id).ToString(),
            Type = p.Type.ToString(),
            Flavor = p.Flavor,
            DisplayName = p.DisplayName,
            Description = Opt(p.Description),
            Enabled = p.Enabled,
            ClientId = string.IsNullOrEmpty(p.ClientId) ? null : p.ClientId,
            // No ClientSecret — encrypted at rest; set one before re-applying to rotate.
            Scopes = p.Scopes,
            FlavorData = p.FlavorData?.RootElement.Clone(),
            UserUpdateScript = string.IsNullOrEmpty(p.UserUpdateScript) ? null : p.UserUpdateScript,
            StoreRawClaims = p.StoreRawClaims,
            RawClaimsRetentionDays = Opt(p.RawClaimsRetentionDays),
            AutoCreateUsers = p.AutoCreateUsers,
            AllowLinking = p.AllowLinking,
            TrustForEmailLink = p.TrustForEmailLink,
            TrustForAuthorization = p.TrustForAuthorization,
            AuthoritativeForProfile = p.AuthoritativeForProfile,
            AllowedEmailDomains = p.AllowedEmailDomains is { Count: > 0 } domains
                ? new Optional<List<string>?>(domains) : default,
            IconName = Opt(p.IconName),
            ButtonColorHex = Opt(p.ButtonColorHex),
        }).ToList();

        // ── Roles (raw — ids are Guids) ──────────────────────────────────────────────
        var roles = await session.Query<PermissionRole>().Where(r => !r.IsDeleted).ToListAsync(ct);
        string? RoleApp(PermissionRole r)
            => !r.IsRealmAdmin && r.AppId is { } aid && appSlugById.TryGetValue(aid, out var slugOf) ? slugOf : null;
        // Group references use the qualified `app/name` key — names repeat across apps.
        var roleKeyById = roles.ToDictionary(r => r.Id, r => RoleKeys.Qualified(RoleApp(r), r.Name));
        var manifestRoles = roles.Select(r => new RealmManifestRole
        {
            Name = r.Name,
            Id = new ShortGuid(r.Id).ToString(),
            Description = Opt(r.Description),
            App = RoleApp(r),
            IsRealmAdmin = r.IsRealmAdmin,
            Permissions = r.IsRealmAdmin
                ? []
                : r.PermissionIds
                    .Where(permKeyById.ContainsKey).Select(id => permKeyById[id]).ToList(),
        }).ToList();

        // ── Users (raw Person for the human list + ApplicationUser for EmailConfirmed) ─
        var persons = await session.Query<Person>().Where(p => !p.IsDeleted).ToListAsync(ct);
        var appUsers = (await session.Query<ApplicationUser>().ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u);
        // The per-user 2FA policy lives on UserSecurityData next to the password hash.
        // Exactly these two fields travel — hashes, stamps and authenticator keys never do.
        var securityById = (await session.Query<UserSecurityData>().ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s);
        var userKeyById = persons.ToDictionary(p => p.Id, p => p.AccountName ?? p.Email ?? p.Id.ToString());
        var manifestUsers = persons.Select(p => new RealmManifestUser
        {
            Key = p.AccountName ?? p.Email,
            Id = new ShortGuid(p.Id).ToString(),
            Firstname = Opt(p.Firstname),
            Lastname = Opt(p.Lastname),
            Acronym = Opt(p.Acronym),
            Email = p.Email ?? string.Empty,
            UserName = p.AccountName,
            // No Password — stored as a hash. Add one before re-applying to set it.
            EmailConfirmed = appUsers.TryGetValue(p.Id, out var au) && au.EmailConfirmed,
            IsActive = appUsers.TryGetValue(p.Id, out var active) ? active.IsActive : p.IsActive,
            GracePeriodDaysOverride = Opt(securityById.TryGetValue(p.Id, out var sec) ? sec.GracePeriodDaysOverride : null),
            TwoFactorExempt = securityById.TryGetValue(p.Id, out var policy) && policy.TwoFactorExempt,
        }).ToList();

        // ── Service accounts — HULLS only (credentials are per-environment secret
        //    material and never exported). The Id IS exported: consuming apps persist
        //    it as their FK, so a stage → prod transfer keeps the same principal id
        //    (the applier pins it at create). ────────────────────────────────────────
        var serviceAccounts = await session.Query<ServiceAccount>()
            .Where(s => !s.IsDeleted).ToListAsync(ct);
        // An account's credentials are the SA-linked clients — skipped in the Clients
        // section above (they are not ordinary clients) and carried here instead, where
        // they belong to the account that owns them. The SECRET never travels: a
        // credential recreated elsewhere is minted a fresh one at apply.
        var credentialsByAccount = (await oauth.GetClientsAsync(
                new PaginationRequest { PageSize = 1000 }, ct))
            .Items.Where(c => c.LinkedServiceAccountId is not null)
            .GroupBy(c => c.LinkedServiceAccountId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var manifestServiceAccounts = serviceAccounts.Select(s => new RealmManifestServiceAccount
        {
            AccountName = s.AccountName,
            Id = new ShortGuid(s.Id).ToString(),
            Purpose = Opt(s.Purpose),
            IsActive = s.IsActive,
            Credentials = credentialsByAccount
                .GetValueOrDefault(new ShortGuid(s.Id).ToString(), [])
                .Select(c => new RealmManifestServiceAccountCredential
                {
                    ClientId = c.ClientId,
                    Id = PinId(c.Id),
                    DisplayName = Opt(c.DisplayName),
                    Scopes = c.Permissions.Where(p => p.StartsWith(ScopePrefix, StringComparison.Ordinal))
                        .Select(p => p[ScopePrefix.Length..]).ToList(),
                    Apps = c.AppIds.Select(id => SlugOfShort(appSlugById, id))
                        .Where(x => x is not null).Select(x => x!).ToList(),
                    Enabled = c.Enabled,
                    AccessTokenType = c.AccessTokenType.ToString(),
                    AccessTokenLifetime = Opt(c.AccessTokenLifetime),
                }).ToList(),
        }).ToList();

        // ── Groups (raw — ids are Guids; resolve members→principal keys, roles→role names) ─
        var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
        // Readable names for every kind of member a group can hold.
        var memberKeyById = new Dictionary<Guid, string>(userKeyById);
        foreach (var g in groups) memberKeyById.TryAdd(g.Id, g.Name);
        foreach (var sa in serviceAccounts) memberKeyById.TryAdd(sa.Id, sa.AccountName);
        var manifestGroups = groups.Select(g => new RealmManifestGroup
        {
            Name = g.Name,
            Id = new ShortGuid(g.Id).ToString(),
            Description = Opt(g.Description),
            // References carry Key + Id: the Id is what the apply follows (rename-proof), the
            // Key is what a human reads. A plain string would ALWAYS mean "key".
            // EVERY member, not just the users. A group may hold nested groups and
            // service accounts, and the domain really expands them (permissions via
            // ApplicationScopeResolver, mail via Group.GetEmailsAsync). Filtering them
            // out here made export -> apply silently DELETE them, because Members is a
            // replace-list. An id names the member; the Key is only the readable half,
            // so a member with no readable name still travels.
            Members = g.MemberIds.Select(id => memberKeyById.TryGetValue(id, out var mk)
                ? ManifestRef.Of(mk, id)
                : new ManifestRef { Id = new ShortGuid(id).ToString() }).ToList(),
            Roles = g.RoleIds.Where(roleKeyById.ContainsKey).Select(id => ManifestRef.Of(roleKeyById[id], id)).ToList(),
            MembershipMode = g.MembershipMode.ToString(),
            MembershipScript = g.MembershipScript,
            Email = Opt(g.Email),
            EmailMode = g.EmailMode.ToString(),
            BoundTo = g.BoundTo.Count == 0 ? null : g.BoundTo,
            ExternallyDrivable = g.ExternallyDrivable,
        }).ToList();

        // ── Positions (MG-FT) — policy + grants are authored config; terminal SLOTS are
        //    device-bound credential material (their clients are excluded above) and are
        //    not exported. Grants reverse to user keys; revoked grants are history. ──────
        var positions = await session.Query<PositionPrincipal>().Where(p => !p.IsDeleted).ToListAsync(ct);
        var manifestPositions = new List<RealmManifestPosition>();
        if (positions.Count > 0)
        {
            var liveGrants = (await session.Query<Modgud.Domain.PositionTerminals.PositionGrant>()
                    .Where(g => g.Status != Modgud.Domain.PositionTerminals.PositionGrantStatus.Revoked)
                    .ToListAsync(ct))
                .ToLookup(g => g.PositionPrincipalId);
            // Terminal slots travel as configuration under their OWNING position (the one
            // the slot was created on); the enrollment, DPoP key and client secret never do.
            var positionKeyById = positions.ToDictionary(p => p.Id, p => p.AccountName);
            var slots = (await session.Query<Modgud.Domain.PositionTerminals.TerminalEnrollment>().ToListAsync(ct))
                .Where(t => t.Status != Modgud.Domain.PositionTerminals.TerminalEnrollmentStatus.Revoked)
                .ToList();
            var slotClientIds = slots.Select(t => t.OAuthApplicationId).ToList();
            var slotClients = slotClientIds.Count == 0
                ? new Dictionary<Guid, Modgud.Domain.OAuth.Applications.OAuthApplicationState>()
                : (await session.LoadManyAsync<Modgud.Domain.OAuth.Applications.OAuthApplicationState>(ct, slotClientIds)).ToDictionary(c => c.Id);
            var slotsByOwner = slots.ToLookup(t => t.PositionPrincipalId);
            List<RealmManifestTerminal> TerminalsOf(Guid ownerId) => slotsByOwner[ownerId]
                .OrderBy(t => t.DisplayName, StringComparer.Ordinal)
                .Select(t =>
                {
                    slotClients.TryGetValue(t.OAuthApplicationId, out var client);
                    return new RealmManifestTerminal
                    {
                        Id = new ShortGuid(t.Id).ToString(),
                        DisplayName = t.DisplayName,
                        Location = Opt(t.Location),
                        WebAuthnRpId = t.WebAuthnRpId,
                        Binding = t.Binding,
                        AllowedPositions = t.EffectiveAllowedPositionIds
                            .Where(id => id != ownerId && positionKeyById.ContainsKey(id))
                            .Select(id => ManifestRef.Of(positionKeyById[id], id)).ToList(),
                        Scopes = client?.Permissions
                            .Where(x => x.StartsWith(ScopePrefix, StringComparison.Ordinal))
                            .Select(x => x[ScopePrefix.Length..]).ToList() ?? [],
                        Apps = client?.AppIds.Where(appSlugById.ContainsKey).Select(id => appSlugById[id]).ToList() ?? [],
                    };
                }).ToList();
            manifestPositions = positions.Select(p => new RealmManifestPosition
            {
                AccountName = p.AccountName,
                Id = new ShortGuid(p.Id).ToString(),
                Purpose = Opt(p.Purpose),
                IsActive = p.IsActive,
                TerminalPolicy = new Application.DTOs.Positions.PositionTerminalPolicyUpdateDto
                {
                    Enabled = p.TerminalPolicy.Enabled,
                    AllowedActivationProofs = p.TerminalPolicy.AllowedActivationProofs.ToList(),
                    AllowedDeviceBindings = p.TerminalPolicy.AllowedDeviceBindings.ToList(),
                    StaffingSessionLifetimeMinutes = (int)p.TerminalPolicy.StaffingSessionLifetime.TotalMinutes,
                    MaximumStaffingSessionLifetimeMinutes = (int)p.TerminalPolicy.MaximumStaffingSessionLifetime.TotalMinutes,
                },
                Grants = liveGrants[p.Id]
                    .Where(g => userKeyById.ContainsKey(g.UserId))
                    .Select(g => ManifestRef.Of(userKeyById[g.UserId], g.UserId)).ToList(),
                Terminals = TerminalsOf(p.Id),
            }).ToList();
        }

        return new RealmManifest
        {
            // No realm shell: slug, routing domains and primary domain are deployment
            // identity, not configuration. Leaving them out is what makes an export
            // portable — the target realm is chosen at apply time, by the route.
            Settings = settings,
            Apps = manifestApps,
            Apis = manifestApis,
            Scopes = manifestScopes,
            Clients = manifestClients,
            Roles = manifestRoles,
            Users = manifestUsers,
            ServiceAccounts = manifestServiceAccounts,
            Groups = manifestGroups,
            LoginProviders = manifestProviders,
            Positions = manifestPositions,
            Jobs = await ExportJobsAsync(sp, ct),
            InboxSettings = await ExportInboxSettingsAsync(session, ct),
        };
    }

    /// <summary>The realm's own jobs, with their current configuration. System jobs (visible
    /// on the control plane) are deployment-wide and no realm's configuration.</summary>
    private static async Task<List<RealmManifestJob>> ExportJobsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var jobs = await sp.GetRequiredService<Modgud.Application.Scheduling.IJobsService>().GetAllAsync(ct);
        return jobs
            .Where(j => string.Equals(j.Scope, "Realm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.Key, StringComparer.Ordinal)
            .Select(j => new RealmManifestJob
            {
                Key = j.Key,
                Enabled = j.Enabled,
                // No override exports as absent (= unchanged on apply), never as an explicit
                // null (= clear) — the same rule as every other optional.
                CronOverride = j.HasOverride ? new Optional<string?>(j.EffectiveCron) : default,
                Parameters = j.Parameters.Count == 0 ? null : new Dictionary<string, object?>(j.Parameters, StringComparer.Ordinal),
            })
            .ToList();
    }

    private static async Task<RealmManifestInboxSettings> ExportInboxSettingsAsync(IDocumentSession session, CancellationToken ct)
    {
        var s = await session.LoadAsync<Modgud.Application.Inbox.InboxRetentionSettings>(
                    Modgud.Application.Inbox.InboxRetentionSettings.SingletonId, ct)
                ?? new Modgud.Application.Inbox.InboxRetentionSettings();
        return new RealmManifestInboxSettings
        {
            AdminChangeRequest = new RealmManifestInboxAdminChangeRequestRetention
            {
                HardDeleteDaysAfterDismissed = s.AdminChangeRequest.HardDeleteDaysAfterDismissed,
            },
            ChangeRequestFeedback = new RealmManifestInboxFeedbackRetention
            {
                MaxUnreadDays = s.ChangeRequestFeedback.MaxUnreadDays,
                AutoExpireDaysAfterRead = s.ChangeRequestFeedback.AutoExpireDaysAfterRead,
            },
            ScheduledJobFeedback = new RealmManifestInboxFeedbackRetention
            {
                MaxUnreadDays = s.ScheduledJobFeedback.MaxUnreadDays,
                AutoExpireDaysAfterRead = s.ScheduledJobFeedback.AutoExpireDaysAfterRead,
            },
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
            // Ids travel (ADR 0024); a group the target does not have is skipped on apply.
            DefaultGroupIds = s.SelfRegistration.DefaultGroupIds,
            TermsOfServiceUrl = Opt(s.SelfRegistration.TermsOfServiceUrl),
            PrivacyPolicyUrl = Opt(s.SelfRegistration.PrivacyPolicyUrl),
            CaptchaEnabled = s.SelfRegistration.CaptchaEnabled,
            CaptchaSiteKey = Opt(s.SelfRegistration.CaptchaSiteKey),
            // CaptchaSecret is write-only (only a CaptchaSecretSet flag is readable) — leave absent.
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
        BrowserSessions = new UpdateBrowserSessionPolicyDto
        {
            IdleLifetimeMinutes = s.BrowserSessions.IdleLifetimeMinutes,
            AbsoluteLifetimeMinutes = s.BrowserSessions.AbsoluteLifetimeMinutes,
            AllowRememberMe = s.BrowserSessions.AllowRememberMe,
        },
        ClientSessions = new UpdateClientSessionPolicyDto
        {
            IdleLifetimeDays = s.ClientSessions.IdleLifetimeDays,
            AbsoluteLifetimeDays = s.ClientSessions.AbsoluteLifetimeDays,
        },
        // Re-applying the exported (unchanged) values yields no consequences, so the
        // ConfirmPositionSecurityConsequences gate stays quiet on an idempotent re-apply.
        PositionSecurity = new UpdatePositionSecuritySettingsDto
        {
            RequiredProofCapabilities = s.PositionSecurity.RequiredProofCapabilities,
            RequiredBindingCapabilities = s.PositionSecurity.RequiredBindingCapabilities,
        },
        // ADR 0019 — only what the realm actually stores (sparse), never the effective
        // defaults: importing must not pin today's defaults as overrides.
        AuthRateLimits = s.AuthRateLimits.Overrides,
        Branding = new UpdateBrandingSettingsDto
        {
            ProductName = Opt(s.Branding.ProductName),
            // Asset ids travel like every other id; the applier skips one the target
            // realm's asset store does not have and keeps the stored value.
            LogoAssetId = Opt(s.Branding.LogoAssetId),
            FaviconAssetId = Opt(s.Branding.FaviconAssetId),
            PrimaryColor = Opt(s.Branding.PrimaryColor),
        },
        EmailBranding = new UpdateEmailBrandingSettingsDto
        {
            ProductName = Opt(s.EmailBranding.ProductName),
            SubjectPrefix = Opt(s.EmailBranding.SubjectPrefix),
            Preheader = Opt(s.EmailBranding.Preheader),
            FooterText = Opt(s.EmailBranding.FooterText),
            FromName = Opt(s.EmailBranding.FromName),
            FromAddress = Opt(s.EmailBranding.FromAddress),
            ReplyTo = Opt(s.EmailBranding.ReplyTo),
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
            SecurityRetentionDays = s.Audit.SecurityRetentionDays,
        },
    };

    /// <summary>
    /// Drops the read-only URLs the settings read shape derives from the asset ids. The ids
    /// themselves travel (ADR 0024 — the applier skips one the target realm does not have);
    /// the URLs are computed on read and would only diff spuriously in a plan.
    /// </summary>
    private static ApplicationSettingsDto WithoutDerivedUrls(ApplicationSettingsDto s) => s with
    {
        Branding = s.Branding is null ? null : s.Branding with { LogoUrl = null, FaviconUrl = null },
    };

    /// <summary>Export-side of the v2 merge-patch contract: a stored null exports as an
    /// ABSENT field (None), never as an explicit null — an explicit null in a manifest
    /// means "clear", and an export must round-trip as "no change". (Beware the implicit
    /// T→Optional operator: a bare assignment would turn null into Some(null) = clear.)</summary>
    private static Optional<string?> Opt(string? value)
        => value is null ? default : new Optional<string?>(value);

    /// <inheritdoc cref="Opt(string)"/>
    private static Optional<int?> Opt(int? value)
        => value is null ? default : new Optional<int?>(value);

    /// <summary>Normalizes an admin-DTO id string (Guid or ShortGuid) to the ShortGuid
    /// form the manifest pins with — the SAME id, exported so a stage → prod transfer
    /// keeps every entity id (consuming apps persist them as FKs).</summary>
    private static string? PinId(string? id)
        => !string.IsNullOrEmpty(id) && ShortGuid.TryParse(id, out Guid g)
            ? new ShortGuid(g).ToString() : null;

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
