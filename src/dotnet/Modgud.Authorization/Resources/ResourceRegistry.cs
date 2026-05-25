namespace Modgud.Authorization.Resources;

/// <summary>
/// Default <see cref="IResourceRegistry"/> backed by an in-memory dictionary
/// keyed by <c>(appSlug, resource) → actions</c>. Populated at startup via
/// <c>opt.RegisterResource("modgud", "user", "read", …)</c> calls on the
/// authorization DI-setup.
/// </summary>
public class ResourceRegistry : IResourceRegistry
{
    private readonly Dictionary<(string App, string Resource), HashSet<string>> _resources
        = new();

    internal void Register(string appSlug, string resourceType, IEnumerable<string> actions)
    {
        var key = (appSlug, resourceType);
        if (!_resources.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _resources[key] = set;
        }
        foreach (var action in actions) set.Add(action);
    }

    public bool IsValidPermission(string permission)
    {
        var parts = permission.Split(':');
        if (parts.Length != 3) return false;
        return _resources.TryGetValue((parts[0], parts[1]), out var actions)
            && actions.Contains(parts[2]);
    }

    public bool IsValidAction(string appSlug, string resourceType, string action)
        => _resources.TryGetValue((appSlug, resourceType), out var actions)
            && actions.Contains(action);

    public IReadOnlyList<string> GetAllPermissions()
        => _resources
            .SelectMany(kv => kv.Value.Select(a => $"{kv.Key.App}:{kv.Key.Resource}:{a}"))
            .ToList();

    public IReadOnlyList<string> GetActionsForResource(string appSlug, string resourceType)
        => _resources.TryGetValue((appSlug, resourceType), out var actions)
            ? actions.ToList()
            : [];

    public IReadOnlyList<string> GetResourceTypes(string appSlug)
        => _resources.Keys
            .Where(k => k.App == appSlug)
            .Select(k => k.Resource)
            .ToList();

    public IReadOnlyList<string> GetAppSlugs()
        => _resources.Keys.Select(k => k.App).Distinct(StringComparer.Ordinal).ToList();
}
