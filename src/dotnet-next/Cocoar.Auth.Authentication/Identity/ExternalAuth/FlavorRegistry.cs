namespace Cocoar.Auth.Authentication.Identity.ExternalAuth;

/// <summary>
/// Runtime lookup for DI-registered <see cref="IIdentityProviderFlavor"/>
/// implementations. Thin wrapper for keyed access that produces a clear error
/// message for unknown keys.
/// </summary>
public class FlavorRegistry
{
    private readonly Dictionary<string, IIdentityProviderFlavor> _byKey;

    public FlavorRegistry(IEnumerable<IIdentityProviderFlavor> flavors)
    {
        _byKey = flavors.ToDictionary(f => f.Key, f => f, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<IIdentityProviderFlavor> All => _byKey.Values;

    public bool TryGet(string key, out IIdentityProviderFlavor flavor)
        => _byKey.TryGetValue(key, out flavor!);

    public IIdentityProviderFlavor Get(string key)
    {
        if (!_byKey.TryGetValue(key, out var flavor))
            throw new KeyNotFoundException(
                $"No IdP flavor registered for key '{key}'. Known: {string.Join(", ", _byKey.Keys)}.");
        return flavor;
    }
}
