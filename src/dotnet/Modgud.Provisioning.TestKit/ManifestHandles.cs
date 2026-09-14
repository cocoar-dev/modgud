namespace Modgud.Provisioning.TestKit;

/// <summary>
/// Translates the kit's name-based authoring into the server's identity contract
/// (ADR 0024 — a manifest identifies by id, never by name).
///
/// <para>A test wants to write <c>Members = ["alice"]</c>, and it should keep being able
/// to. But the server no longer resolves that name against the target realm, for a good
/// reason: the "alice" over there need not be this alice. So every entity the manifest
/// DECLARES and does not pin to a real id gets a document-local <c>#handle</c>, and every
/// reference to a declared entity is rewritten to point at that handle. The result means
/// exactly what the test wrote — "the alice in THIS file" — and can never adopt a stranger
/// who merely shares the name.</para>
///
/// <para>A reference to something the manifest does not declare is left as written. It then
/// fails at the server with <c>Manifest.ReferenceByName</c>, naming the problem precisely:
/// this kit provisions a realm from a file, so a reference has to point at something the
/// file contains — or at a real id the author supplies.</para>
/// </summary>
internal static class ManifestHandles
{
    internal static RealmManifest ForWire(RealmManifest manifest)
    {
        // Handles are namespaced per entity kind: a role and a user may share a key
        // without colliding, and a handle stays readable in a server error message.
        // key/alias -> the reference to send. A handle goes out as a bare "#..." string;
        // a pinned REAL id has to go out as { Key, Id }, because a bare string without '#'
        // reads back as a name and the server refuses names.
        var roleHandles = new Dictionary<string, ManifestRef>(StringComparer.OrdinalIgnoreCase);
        var userHandles = new Dictionary<string, ManifestRef>(StringComparer.OrdinalIgnoreCase);

        var roles = new List<RealmManifestRole>(manifest.Roles.Count);
        foreach (var r in manifest.Roles)
        {
            var natural = r.App is { Length: > 0 } app ? $"{app}/{r.Name}" : r.Name;
            var id = r.Id ?? $"#role:{natural}";
            // Every spelling a group may use for this role: the explicit Key, the qualified
            // key, and the bare name (which the kit has always accepted).
            Register(roleHandles, new ManifestRef { Key = r.Id is null ? null : natural, Id = id },
                r.Key, natural, r.Name);
            roles.Add(r.Id is null ? r with { Id = id } : r);
        }

        var users = new List<RealmManifestUser>(manifest.Users.Count);
        foreach (var u in manifest.Users)
        {
            var natural = u.Key ?? u.UserName ?? u.Email;
            var id = u.Id ?? $"#user:{natural}";
            Register(userHandles, new ManifestRef { Key = u.Id is null ? null : natural, Id = id },
                u.Key, natural, u.UserName, u.Email);
            users.Add(u.Id is null ? u with { Id = id } : u);
        }

        var groups = manifest.Groups
            .Select(g => g with
            {
                Id = g.Id ?? $"#group:{g.Name}",
                Members = g.Members.Select(m => Resolve(userHandles, m)).ToList(),
                Roles = g.Roles.Select(r => Resolve(roleHandles, r)).ToList(),
            })
            .ToList();

        return manifest with
        {
            Apps = manifest.Apps.Select(a => a.Id is null ? a with { Id = $"#app:{a.Slug}" } : a).ToList(),
            Apis = manifest.Apis.Select(a => a.Id is null ? a with { Id = $"#api:{a.Name}" } : a).ToList(),
            Scopes = manifest.Scopes.Select(s => s.Id is null ? s with { Id = $"#scope:{s.Name}" } : s).ToList(),
            Clients = manifest.Clients.Select(c => c.Id is null ? c with { Id = $"#client:{c.ClientId}" } : c).ToList(),
            Roles = roles,
            Users = users,
            Groups = groups,
        };
    }

    /// <summary>Rewrites an authored reference to the form the server accepts, leaving one
    /// it does not recognise untouched so the server can name the problem.</summary>
    private static ManifestRef Resolve(IReadOnlyDictionary<string, ManifestRef> map, ManifestRef authored)
        => authored.Key is { Length: > 0 } key && map.TryGetValue(key, out var wire) ? wire : authored;

    /// <summary>Records every alias a reference may use for one entity. First declaration
    /// wins: with two entities sharing an alias the ambiguous spelling stays pointing at
    /// the first, and the second is still reachable by its own unambiguous key.</summary>
    private static void Register(
        Dictionary<string, ManifestRef> map, ManifestRef wire, params string?[] aliases)
    {
        foreach (var alias in aliases)
            if (!string.IsNullOrEmpty(alias)) map.TryAdd(alias, wire);
    }
}
