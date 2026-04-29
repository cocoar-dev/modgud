namespace Cocoar.Auth.Authorization.Resources;

/// <summary>
/// Default <see cref="IResourceRegistry"/> backed by an in-memory dictionary.
/// Populated at startup via <c>opt.RegisterResource("todo", "read", …)</c> calls
/// on the authorization DI-setup.
/// </summary>
public class ResourceRegistry : IResourceRegistry
{
    private readonly Dictionary<string, HashSet<string>> _resources = new(StringComparer.Ordinal);

    internal void Register(string resourceType, IEnumerable<string> actions)
    {
        if (!_resources.TryGetValue(resourceType, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _resources[resourceType] = set;
        }
        foreach (var action in actions) set.Add(action);
    }

    public bool IsValidPermission(string permission)
    {
        var parts = permission.Split(':');
        if (parts.Length != 2) return false;
        return _resources.TryGetValue(parts[0], out var actions) && actions.Contains(parts[1]);
    }

    public bool IsValidAction(string resourceType, string action)
        => _resources.TryGetValue(resourceType, out var actions) && actions.Contains(action);

    public IReadOnlyList<string> GetAllPermissions()
        => _resources
            .SelectMany(kv => kv.Value.Select(a => $"{kv.Key}:{a}"))
            .ToList();

    public IReadOnlyList<string> GetActionsForResource(string resourceType)
        => _resources.TryGetValue(resourceType, out var actions)
            ? actions.ToList()
            : [];

    public IReadOnlyList<string> GetResourceTypes()
        => _resources.Keys.ToList();
}
