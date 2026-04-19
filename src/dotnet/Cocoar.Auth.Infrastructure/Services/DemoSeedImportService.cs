using System.Text.Json;
using Cocoar.Auth.Application.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Principals;
using Cocoar.Auth.Infrastructure.Authorization;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Imports the ABAC-flavoured demo seed from <c>data/demo-seed.json</c>: a handful
/// of Persons, a few <see cref="PermissionRole"/>s, and a selection of
/// <see cref="AuthorizationGroup"/>s that showcase manual, auto-membership and
/// nested-group patterns. Intended for the bootstrap admin's first-use experience —
/// see <see cref="Cocoar.Auth.Application.DTOs.Auth.CurrentUserDto"/>'s <c>LoadDemoData</c>
/// flag on the setup endpoint.
/// <para>
/// String references in the JSON use a <c>@key</c> convention — <c>@alice</c> is
/// resolved by looking up "alice" first in the user-key map, then the group-key
/// map. Ambiguity would silently pick the user side; the seed JSON is authored
/// so keys don't collide.
/// </para>
/// </summary>
public class DemoSeedImportService(
    UserManager<ApplicationUser> userManager,
    IDocumentSession session,
    IMembershipEvaluator membershipEvaluator,
    AutoMembershipRecalculator autoMembershipRecalculator,
    ILogger<DemoSeedImportService> logger)
{
    private readonly Dictionary<string, Guid> _userIds = new();
    private readonly Dictionary<string, Guid> _roleIds = new();
    private readonly Dictionary<string, Guid> _groupIds = new();

    public async Task<DemoSeedResult> ImportAsync(string jsonPath, CancellationToken ct = default)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"Demo seed file not found: {jsonPath}");

        var json = await File.ReadAllTextAsync(jsonPath, ct);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var data = JsonSerializer.Deserialize<DemoSeedData>(json, opts)
            ?? throw new InvalidOperationException("demo-seed.json did not deserialize.");

        await ImportUsersAsync(data, ct);
        ImportPermissionRoles(data);
        await session.SaveChangesAsync(ct);

        var autoGroups = ImportGroups(data);
        await session.SaveChangesAsync(ct);

        // PrincipalDirectory is an inline projection that observed the UserCreated events
        // from the user-manager step. Auto-groups can now be resolved against it in one
        // SQL per group; the recalculator appends a MembershipRecomputed event where the
        // initial MemberIds list differs from the query result.
        foreach (var g in autoGroups)
        {
            await autoMembershipRecalculator.RecalculateForGroupAsync(g, session, ct);
        }
        if (autoGroups.Count > 0)
            await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "[DemoSeed] Imported {Users} users, {Roles} permission roles, {Groups} groups ({Auto} auto)",
            _userIds.Count, _roleIds.Count, _groupIds.Count, autoGroups.Count);

        return new DemoSeedResult(
            Users: _userIds.Count,
            PermissionRoles: _roleIds.Count,
            AuthorizationGroups: _groupIds.Count,
            AutoGroups: autoGroups.Count,
            DefaultPassword: data.Password);
    }

    private async Task ImportUsersAsync(DemoSeedData data, CancellationToken ct)
    {
        foreach (var u in data.Users)
        {
            var appUser = new ApplicationUser(u.UserName, u.Email);
            if (!string.IsNullOrWhiteSpace(u.FirstName)) appUser.SetFirstName(u.FirstName);
            if (!string.IsNullOrWhiteSpace(u.LastName)) appUser.SetLastName(u.LastName);

            var result = await userManager.CreateAsync(appUser, data.Password);
            if (!result.Succeeded)
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Demo seed: could not create user '{u.UserName}': {errors}");
            }
            _userIds[u.Key] = appUser.Id;
        }
    }

    private void ImportPermissionRoles(DemoSeedData data)
    {
        foreach (var r in data.PermissionRoles)
        {
            var id = Guid.CreateVersion7();
            _roleIds[r.Key] = id;
            session.Events.StartStream<PermissionRole>(id,
                new PermissionRoleCreatedEvent(
                    Id: id,
                    Name: r.Name,
                    Description: r.Description,
                    ResourceType: r.ResourceType,
                    Permissions: r.Permissions));
        }
    }

    /// <summary>
    /// Creates the AuthorizationGroups in two passes: first all non-auto and
    /// auto groups emit their Created event with the raw memberIds / roleIds
    /// resolved. Groups that are themselves used as members of later groups are
    /// resolved via <see cref="_groupIds"/> which grows as we iterate — so the
    /// JSON order matters: a group referenced as a nested member of another
    /// group must appear first.
    /// </summary>
    private List<AuthorizationGroup> ImportGroups(DemoSeedData data)
    {
        var autoGroups = new List<AuthorizationGroup>();

        foreach (var g in data.AuthorizationGroups)
        {
            var id = Guid.CreateVersion7();
            _groupIds[g.Key] = id;

            var memberIds = (g.MemberIds ?? []).Select(ResolvePrincipalKey).ToList();
            var roleIds = (g.RoleIds ?? []).Select(ResolveRoleKey).ToList();
            var mode = string.Equals(g.MembershipMode, "Auto", StringComparison.OrdinalIgnoreCase)
                ? MembershipMode.Auto
                : MembershipMode.Manual;

            string? membershipScript = null;
            string? compiledMembershipScript = null;
            List<string>? membershipDeps = null;
            if (mode == MembershipMode.Auto && !string.IsNullOrWhiteSpace(g.MembershipScript))
            {
                membershipScript = g.MembershipScript;
                try
                {
                    compiledMembershipScript = membershipEvaluator.TranspileMembershipScript(g.MembershipScript);
                    var deps = membershipEvaluator.CollectDependencies<IPrincipal>(compiledMembershipScript, "PrincipalDirectory");
                    membershipDeps = deps?.ToList();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[DemoSeed] Script transpile failed for group '{Group}'; stored raw source only.", g.Name);
                }
            }

            session.Events.StartStream<AuthorizationGroup>(id,
                new AuthorizationGroupCreatedEvent(
                    Id: id,
                    Name: g.Name,
                    Description: g.Description,
                    MemberIds: memberIds,
                    RoleIds: roleIds,
                    AccessScripts: [],
                    MembershipMode: mode,
                    MembershipScript: membershipScript,
                    CompiledMembershipScript: compiledMembershipScript,
                    MembershipScriptDependencies: membershipDeps,
                    Email: null,
                    EmailMode: EmailMode.Shared));

            if (mode == MembershipMode.Auto)
            {
                // Build an in-memory AuthorizationGroup for the recalculator — the
                // inline projection would give us the same object after SaveChanges
                // but we already have every field, so skip the reload.
                autoGroups.Add(new AuthorizationGroup
                {
                    Id = id,
                    Name = g.Name,
                    Description = g.Description,
                    MemberIds = memberIds,
                    RoleIds = roleIds,
                    AccessScripts = [],
                    MembershipMode = mode,
                    MembershipScript = membershipScript,
                    CompiledMembershipScript = compiledMembershipScript,
                    MembershipScriptDependencies = membershipDeps,
                });
            }
        }

        return autoGroups;
    }

    private Guid ResolveRoleKey(string reference)
    {
        var key = StripAt(reference);
        if (_roleIds.TryGetValue(key, out var id)) return id;
        throw new InvalidOperationException($"Demo seed: role reference '{reference}' not found. Declare the role before the group that uses it.");
    }

    private Guid ResolvePrincipalKey(string reference)
    {
        var key = StripAt(reference);
        if (_userIds.TryGetValue(key, out var uid)) return uid;
        if (_groupIds.TryGetValue(key, out var gid)) return gid;
        throw new InvalidOperationException($"Demo seed: principal reference '{reference}' matches neither a user nor a group. Declare it before the group that uses it.");
    }

    private static string StripAt(string reference)
        => reference.StartsWith('@') ? reference[1..] : reference;
}

public record DemoSeedResult(
    int Users,
    int PermissionRoles,
    int AuthorizationGroups,
    int AutoGroups,
    string DefaultPassword);

// ── JSON shapes ────────────────────────────────────────────────────────

internal class DemoSeedData
{
    public string Password { get; set; } = "Demo1234!";
    public List<DemoUser> Users { get; set; } = [];
    public List<DemoPermissionRole> PermissionRoles { get; set; } = [];
    public List<DemoAuthorizationGroup> AuthorizationGroups { get; set; } = [];
}

internal class DemoUser
{
    public string Key { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
}

internal class DemoPermissionRole
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ResourceType { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
}

internal class DemoAuthorizationGroup
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? MemberIds { get; set; }
    public List<string>? RoleIds { get; set; }
    public string? MembershipMode { get; set; } // "Manual" | "Auto"
    public string? MembershipScript { get; set; }
}
