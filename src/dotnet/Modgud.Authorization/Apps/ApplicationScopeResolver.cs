using System.Security.Cryptography;
using System.Text;
using Marten;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;

namespace Modgud.Authorization.Apps;

/// <summary>
/// Resolves the business-principal slice of one Application from the existing
/// group graph. A live, active group is a root when its <c>BoundTo</c> contains
/// the Application slug or <c>*</c>. The scope is the transitive closure of
/// those roots and their members, across every shipped Principal type.
/// </summary>
public interface IApplicationScopeResolver
{
    Task<ApplicationScopeSnapshot?> ResolveAsync(Guid appId, CancellationToken ct = default);
}

public sealed record ApplicationScopeSnapshot(
    Guid AppId,
    string AppSlug,
    string ScopeVersion,
    IReadOnlyList<Group> RootGroups,
    IReadOnlyList<Principal> Principals);

public sealed class ApplicationScopeResolver(IQuerySession session) : IApplicationScopeResolver
{
    public async Task<ApplicationScopeSnapshot?> ResolveAsync(
        Guid appId,
        CancellationToken ct = default)
    {
        var app = await session.LoadAsync<App>(appId, ct);
        if (app is null || app.IsDeleted) return null;

        // One directory query gives the version and all members one consistent
        // database snapshot. A multi-page/multi-query walk could otherwise pair
        // an old root set with new membership (or vice versa).
        var principals = await session.Query<Principal>()
            .Where(p => !p.IsDeleted && p.IsActive)
            .ToListAsync(ct);

        return BuildSnapshot(app, principals);
    }

    internal static ApplicationScopeSnapshot BuildSnapshot(
        App app,
        IReadOnlyCollection<Principal> directory)
    {
        var activeDirectory = directory
            .Where(p => !p.IsDeleted && p.IsActive)
            .ToList();
        var byId = activeDirectory.ToDictionary(p => p.Id);
        var roots = activeDirectory
            .OfType<Group>()
            .Where(g => g.BoundTo.Contains(PermissionService.AllAppsWildcard, StringComparer.Ordinal)
                        || g.BoundTo.Contains(app.Slug, StringComparer.Ordinal))
            .OrderBy(g => g.Id)
            .ToList();

        var included = new HashSet<Guid>();
        var pendingGroups = new Queue<Guid>();
        foreach (var root in roots)
        {
            if (included.Add(root.Id)) pendingGroups.Enqueue(root.Id);
        }

        while (pendingGroups.TryDequeue(out var groupId))
        {
            if (!byId.TryGetValue(groupId, out var candidate) || candidate is not Group group)
                continue;

            foreach (var memberId in group.MemberIds)
            {
                if (!byId.TryGetValue(memberId, out var member)) continue;
                if (!included.Add(memberId)) continue;
                if (member is Group) pendingGroups.Enqueue(memberId);
            }
        }

        var scopedPrincipals = included
            .Select(id => byId[id])
            .OrderBy(p => p.Type, StringComparer.Ordinal)
            .ThenBy(p => p.Id)
            .ToList();
        var scopedGroups = scopedPrincipals.OfType<Group>().ToList();

        return new ApplicationScopeSnapshot(
            app.Id,
            app.Slug,
            BuildVersion(roots, scopedGroups),
            roots,
            scopedPrincipals);
    }

    /// <summary>
    /// Opaque definition version derived from the bound root set, nested-group
    /// structure, and automatic-membership predicates. Ordinary direct member
    /// changes deliberately leave it stable and will be represented as individual
    /// events by the resumable change stream.
    /// </summary>
    internal static string BuildVersion(
        IEnumerable<Group> rootGroups,
        IEnumerable<Group> scopedGroups)
    {
        var roots = rootGroups.Select(g => g.Id).ToHashSet();
        var groups = scopedGroups
            .GroupBy(g => g.Id)
            .Select(g => g.First())
            .OrderBy(g => g.Id)
            .ToList();
        var groupIds = groups.Select(g => g.Id).ToHashSet();

        var lines = new List<string>(groups.Count * 2);
        lines.AddRange(roots.OrderBy(id => id).Select(id => $"root:{id:N}"));
        foreach (var group in groups)
        {
            var script = group.MembershipMode == MembershipMode.Auto
                ? group.MembershipScript ?? string.Empty
                : string.Empty;
            var scriptDigest = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(script)));
            var nestedGroupIds = group.MemberIds
                .Where(groupIds.Contains)
                .Distinct()
                .OrderBy(id => id)
                .Select(id => id.ToString("N"));
            lines.Add($"group:{group.Id:N}|mode:{group.MembershipMode}|script:{scriptDigest}");
            lines.Add($"children:{group.Id:N}|{string.Join(',', nestedGroupIds)}");
        }

        var canonical = string.Join('\n', lines);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"modgud-app-scope-v1\n{canonical}"));
        return $"v1-{Convert.ToHexStringLower(digest)}";
    }
}
