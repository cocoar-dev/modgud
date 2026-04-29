using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.DTOs.OAuth;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authorization.Access;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Domain.Identity.LoginProviders;

namespace Cocoar.Auth.Api.Features.Dev;

/// <summary>
/// Loads <c>data/demo-seed.json</c> into the active tenant. Designed to run
/// once after first-time setup so developers/testers immediately get a
/// realistic data set: extra users, granular permission roles, manual + auto
/// authorization groups, custom OAuth scopes / clients / API and a sample
/// (deactivated) external login provider.
///
/// <para>Idempotent — re-runs skip entities that already exist by their
/// natural key (username, role name, group name, scope name, client_id,
/// API name, login-provider name).</para>
/// </summary>
public sealed class DemoSeedService : IDemoSeedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DemoSeedService> _logger;

    // Resolved-id tables — populated as we create entities so later phases can
    // wire references without touching the JSON.
    private readonly Dictionary<string, Guid> _userIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _roleIds = new(StringComparer.OrdinalIgnoreCase);

    public DemoSeedService(IServiceProvider services, ILogger<DemoSeedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<object> ImportAsync(string jsonPath)
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        var data = JsonSerializer.Deserialize<DemoSeedData>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Failed to parse demo seed JSON.");

        var counts = new SeedCounts();

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        var session = sp.GetRequiredService<IDocumentSession>();
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var membershipEvaluator = sp.GetRequiredService<IMembershipEvaluator>();
        var autoMembershipRecalculator = sp.GetRequiredService<IAutoMembershipRecalculator>();
        var oauthAdmin = sp.GetRequiredService<OAuthAdminService>();
        var loginProviderService = sp.GetRequiredService<LoginProviderService>();

        _logger.LogInformation("[DemoSeed] Starting demo data import from {Path}", jsonPath);

        // ── Phase 1: PermissionRoles ────────────────────────────────────────
        // Index existing roles so we can resolve "@SystemAdmin" / "@UserManager"
        // / "@Viewer" references created by Setup, and skip duplicates by name.
        await IndexExistingRolesAsync(session, default);
        foreach (var r in data.Roles)
        {
            if (_roleIds.TryGetValue(r.Name, out _))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing role '{Name}'", r.Name);
                continue;
            }

            var role = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = r.Name,
                Description = r.Description,
                AppSlug = AppSlugs.CocoarAuth,
                ResourceType = string.IsNullOrWhiteSpace(r.Resource) ? "app" : r.Resource,
                Permissions = r.Permissions ?? new List<string>(),
            };
            session.Store(role);
            session.Events.StartStream(role.Id,
                new PermissionRoleCreatedEvent(role.Id, role.Name, role.Description, role.AppSlug, role.ResourceType, role.Permissions));

            _roleIds[role.Name] = role.Id;
            // Also expose via the JSON key so the groups section can reference
            // either the human-readable name or the seed-key.
            _roleIds[r.Key] = role.Id;
            counts.Roles++;
        }
        await session.SaveChangesAsync();

        // ── Phase 2: Users ──────────────────────────────────────────────────
        // Skip users that already exist by username — re-runs are then no-ops.
        var existingUsers = await session.Query<ApplicationUser>()
            .Where(u => !u.IsDeleted)
            .ToListAsync();
        foreach (var u in existingUsers)
            _userIds[u.UserName] = u.Id;

        foreach (var u in data.Users)
        {
            var userName = (u.UserName ?? u.Key).Trim().ToLowerInvariant();
            if (_userIds.ContainsKey(userName) || _userIds.ContainsKey(u.Key))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing user '{UserName}'", userName);
                _userIds[u.Key] = _userIds.TryGetValue(userName, out var existingId) ? existingId : _userIds[u.Key];
                continue;
            }

            var appUser = new ApplicationUser(userName, u.Email)
            {
                Id = Guid.NewGuid(),
                Firstname = u.Firstname,
                Lastname = u.Lastname,
                Acronym = u.Acronym,
                EmailConfirmed = true,
                IsActive = true,
            };
            var result = await userManager.CreateAsync(appUser, data.Password);
            if (!result.Succeeded)
            {
                var msg = string.Join("; ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("[DemoSeed] Could not create user '{UserName}': {Errors}", userName, msg);
                continue;
            }

            _userIds[u.Key] = appUser.Id;
            _userIds[userName] = appUser.Id;
            counts.Users++;
        }

        // ── Phase 3: Groups ─────────────────────────────────────────────────
        // We create groups idempotently by name. Auto-membership groups get an
        // initial recalculate-pass so the Person directory we just populated
        // is reflected in the MemberIds.
        var existingGroups = await session.Query<Group>()
            .Where(g => !g.IsDeleted)
            .ToListAsync();
        var groupsByName = existingGroups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var autoGroupsToRecalc = new List<Group>();

        // Reload roles so Setup-created roles ("System Admin" / "User Manager"
        // / "Viewer") are in the lookup table.
        await IndexExistingRolesAsync(session, default);

        foreach (var g in data.Groups)
        {
            if (groupsByName.ContainsKey(g.Name))
            {
                _logger.LogInformation("[DemoSeed] Group '{Name}' already exists — also adding it to the recalc list if Auto.", g.Name);
                if (string.Equals(g.MembershipMode, "Auto", StringComparison.OrdinalIgnoreCase))
                    autoGroupsToRecalc.Add(groupsByName[g.Name]);
                continue;
            }

            var memberIds = ResolveMembers(g.Members);
            var roleIds = ResolveRoles(g.Roles);

            var mode = string.Equals(g.MembershipMode, "Auto", StringComparison.OrdinalIgnoreCase)
                ? MembershipMode.Auto
                : MembershipMode.Manual;

            string? compiledScript = null;
            List<string>? scriptDeps = null;
            if (mode == MembershipMode.Auto && !string.IsNullOrWhiteSpace(g.MembershipScript))
            {
                try
                {
                    compiledScript = membershipEvaluator.TranspileMembershipScript(g.MembershipScript);
                    scriptDeps = membershipEvaluator.CollectDependencies<Principal>(compiledScript)?.ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DemoSeed] Failed to compile membership script for '{Name}', creating group anyway.", g.Name);
                }
            }

            var group = new Group
            {
                Id = Guid.NewGuid(),
                Name = g.Name,
                Description = g.Description,
                MemberIds = memberIds,
                RoleIds = roleIds,
                AccessScripts = new List<ResourceAccessScript>(),
                MembershipMode = mode,
                MembershipScript = mode == MembershipMode.Auto ? g.MembershipScript : null,
                CompiledMembershipScript = compiledScript,
                MembershipScriptDependencies = scriptDeps,
                BoundTo = [AppSlugs.CocoarAuth],
            };
            session.Store(group);
            session.Events.StartStream(group.Id,
                new GroupCreatedEvent(
                    group.Id, group.Name, group.Description,
                    group.MemberIds, group.RoleIds, group.AccessScripts,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode,
                    group.BoundTo));
            groupsByName[group.Name] = group;
            counts.Groups++;

            if (mode == MembershipMode.Auto)
                autoGroupsToRecalc.Add(group);
        }
        await session.SaveChangesAsync();

        // Auto-membership recalc runs one SQL query per auto-group against the
        // Principal directory. Because the group projection is inline, the
        // resulting member-ids are visible immediately after SaveChanges.
        foreach (var g in autoGroupsToRecalc)
        {
            try
            {
                await autoMembershipRecalculator.RecalculateForGroupAsync(g, session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DemoSeed] Auto-membership recalc failed for group '{Name}'", g.Name);
            }
        }
        if (autoGroupsToRecalc.Count > 0)
            await session.SaveChangesAsync();

        // ── Phase 4: OAuth Scopes ───────────────────────────────────────────
        var existingScopes = await oauthAdmin.GetScopesAsync();
        var existingScopeNames = existingScopes.Items.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in data.Scopes)
        {
            if (existingScopeNames.Contains(s.Name))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing scope '{Name}'", s.Name);
                continue;
            }
            var dto = new CreateOAuthScopeDto
            {
                Name = s.Name,
                DisplayName = s.DisplayName,
                Description = s.Description,
                Resources = s.Resources?.Count > 0 ? s.Resources : new List<string> { "demo-api" },
                Enabled = true,
                ShowInDiscoveryDocument = true,
            };
            var result = await oauthAdmin.CreateScopeAsync(dto);
            if (result.IsError)
            {
                _logger.LogWarning("[DemoSeed] Could not create scope '{Name}': {Error}",
                    s.Name, result.FirstError.Description);
                continue;
            }
            counts.Scopes++;
        }

        // ── Phase 5: OAuth APIs ─────────────────────────────────────────────
        var existingApis = await oauthAdmin.GetApisAsync(new PaginationRequest { PageSize = 100 });
        var existingApiNames = existingApis.Items.Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apiSecrets = new Dictionary<string, string>();
        foreach (var a in data.Apis)
        {
            if (existingApiNames.Contains(a.Name))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing API '{Name}'", a.Name);
                continue;
            }
            var dto = new CreateOAuthApiDto
            {
                Name = a.Name,
                DisplayName = a.DisplayName,
                Description = a.Description,
                Enabled = true,
                Scopes = a.Scopes ?? new List<string>(),
                UserClaims = a.UserClaims ?? new List<string>(),
            };
            var result = await oauthAdmin.CreateApiAsync(dto);
            if (result.IsError)
            {
                _logger.LogWarning("[DemoSeed] Could not create API '{Name}': {Error}",
                    a.Name, result.FirstError.Description);
                continue;
            }
            apiSecrets[a.Name] = result.Value.ApiSecret;
            counts.Apis++;
        }

        // ── Phase 6: OAuth Clients ──────────────────────────────────────────
        var existingClients = await oauthAdmin.GetClientsAsync(new PaginationRequest { PageSize = 100 });
        var existingClientIds = existingClients.Items.Select(c => c.ClientId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clientSecrets = new Dictionary<string, string>();
        foreach (var c in data.Clients)
        {
            if (existingClientIds.Contains(c.ClientId))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing client '{ClientId}'", c.ClientId);
                continue;
            }
            var dto = new CreateOAuthClientDto
            {
                ClientId = c.ClientId,
                DisplayName = c.DisplayName,
                ClientType = c.ClientType,
                ClientSecret = c.ClientSecret,
                ConsentType = string.IsNullOrWhiteSpace(c.ConsentType) ? "implicit" : c.ConsentType,
                RedirectUris = c.RedirectUris ?? new List<string>(),
                PostLogoutRedirectUris = c.PostLogoutRedirectUris ?? new List<string>(),
                AllowedGrantTypes = c.AllowedGrantTypes ?? new List<string>(),
                Scopes = c.Scopes ?? new List<string>(),
                RequireConsent = c.RequireConsent ?? false,
                RequireClientSecret = c.RequireClientSecret ?? (c.ClientType == "confidential"),
                Enabled = true,
            };
            var result = await oauthAdmin.CreateClientAsync(dto);
            if (result.IsError)
            {
                _logger.LogWarning("[DemoSeed] Could not create client '{ClientId}': {Error}",
                    c.ClientId, result.FirstError.Description);
                continue;
            }
            if (result.Value.ClientSecret is { Length: > 0 } secret)
                clientSecrets[c.ClientId] = secret;
            counts.Clients++;
        }

        // ── Phase 7: Login Providers ────────────────────────────────────────
        var existingProviders = await loginProviderService.GetAllAsync();
        var existingProviderNames = existingProviders.Items.Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.LoginProviders)
        {
            if (existingProviderNames.Contains(p.Name))
            {
                _logger.LogInformation("[DemoSeed] Skipping existing login provider '{Name}'", p.Name);
                continue;
            }
            if (!Enum.TryParse<LoginProviderType>(p.Type, ignoreCase: true, out var type))
                type = LoginProviderType.OpenIdConnect;
            var dto = new CreateLoginProviderDto
            {
                Name = p.Name,
                DisplayName = p.DisplayName,
                Description = p.Description,
                Type = type,
                Configuration = p.Configuration ?? new Dictionary<string, string>(),
            };
            var result = await loginProviderService.CreateAsync(dto);
            if (result.IsError)
            {
                _logger.LogWarning("[DemoSeed] Could not create login provider '{Name}': {Error}",
                    p.Name, result.FirstError.Description);
                continue;
            }
            counts.LoginProviders++;
        }

        _logger.LogInformation(
            "[DemoSeed] Done — users={Users}, roles={Roles}, groups={Groups}, scopes={Scopes}, apis={Apis}, clients={Clients}, loginProviders={LoginProviders}",
            counts.Users, counts.Roles, counts.Groups, counts.Scopes, counts.Apis, counts.Clients, counts.LoginProviders);

        return new
        {
            Message = "Demo data seeded",
            counts.Users,
            counts.Roles,
            counts.Groups,
            counts.Scopes,
            counts.Apis,
            counts.Clients,
            counts.LoginProviders,
            Password = data.Password,
            ClientSecrets = clientSecrets,
            ApiSecrets = apiSecrets,
        };
    }

    private async Task IndexExistingRolesAsync(IDocumentSession session, CancellationToken ct)
    {
        var existing = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted)
            .ToListAsync(ct);
        foreach (var r in existing)
        {
            // Map BOTH the human name and a stable "@PascalKey" form so the JSON
            // can reference Setup-created roles via "@SystemAdmin", "@UserManager",
            // "@Viewer" without hard-coding their GUIDs.
            _roleIds[r.Name] = r.Id;
            _roleIds["@" + r.Name.Replace(" ", "")] = r.Id;
        }
    }

    private List<Guid> ResolveMembers(IEnumerable<string>? keys)
    {
        var list = new List<Guid>();
        if (keys is null) return list;
        foreach (var k in keys)
        {
            if (_userIds.TryGetValue(k, out var id))
                list.Add(id);
            else
                _logger.LogWarning("[DemoSeed] Unknown member key '{Key}' — skipping.", k);
        }
        return list;
    }

    private List<Guid> ResolveRoles(IEnumerable<string>? keys)
    {
        var list = new List<Guid>();
        if (keys is null) return list;
        foreach (var k in keys)
        {
            if (_roleIds.TryGetValue(k, out var id))
                list.Add(id);
            else
                _logger.LogWarning("[DemoSeed] Unknown role key '{Key}' — skipping.", k);
        }
        return list;
    }

    private sealed class SeedCounts
    {
        public int Users;
        public int Roles;
        public int Groups;
        public int Scopes;
        public int Apis;
        public int Clients;
        public int LoginProviders;
    }

    // ── JSON DTOs ───────────────────────────────────────────────────────────

    private sealed record DemoSeedData
    {
        public string Password { get; init; } = "Demo1234!";
        public List<DemoUser> Users { get; init; } = new();
        public List<DemoRole> Roles { get; init; } = new();
        public List<DemoGroup> Groups { get; init; } = new();
        public List<DemoScope> Scopes { get; init; } = new();
        public List<DemoApi> Apis { get; init; } = new();
        public List<DemoClient> Clients { get; init; } = new();
        public List<DemoLoginProvider> LoginProviders { get; init; } = new();
    }

    private sealed record DemoUser
    {
        public string Key { get; init; } = "";
        public string? UserName { get; init; }
        public string? Firstname { get; init; }
        public string? Lastname { get; init; }
        public string? Acronym { get; init; }
        public string? Email { get; init; }
        public bool IsAdmin { get; init; }
    }

    private sealed record DemoRole
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Resource { get; init; } = "app";
        public List<string> Permissions { get; init; } = new();
    }

    private sealed record DemoGroup
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public List<string> Members { get; init; } = new();
        public List<string> Roles { get; init; } = new();
        public string? MembershipMode { get; init; }
        public string? MembershipScript { get; init; }
    }

    private sealed record DemoScope
    {
        public string Name { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public List<string>? Resources { get; init; }
    }

    private sealed record DemoApi
    {
        public string Name { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public List<string>? Scopes { get; init; }
        public List<string>? UserClaims { get; init; }
    }

    private sealed record DemoClient
    {
        public string ClientId { get; init; } = "";
        public string? DisplayName { get; init; }
        public string ClientType { get; init; } = "public";
        public string? ClientSecret { get; init; }
        public string? ConsentType { get; init; }
        public List<string>? RedirectUris { get; init; }
        public List<string>? PostLogoutRedirectUris { get; init; }
        public List<string>? AllowedGrantTypes { get; init; }
        public List<string>? Scopes { get; init; }
        public bool? RequireConsent { get; init; }
        public bool? RequireClientSecret { get; init; }
    }

    private sealed record DemoLoginProvider
    {
        public string Name { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public string Type { get; init; } = "OpenIdConnect";
        public Dictionary<string, string>? Configuration { get; init; }
    }
}
