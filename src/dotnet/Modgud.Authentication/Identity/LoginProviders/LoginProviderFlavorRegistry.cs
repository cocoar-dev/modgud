namespace Modgud.Authentication.Identity.LoginProviders;

/// <summary>
/// Runtime lookup for DI-registered <see cref="ILoginProviderFlavor"/>
/// implementations. Thin wrapper for keyed access that produces a clear error
/// message for unknown keys.
/// </summary>
public class LoginProviderFlavorRegistry
{
    private readonly Dictionary<string, ILoginProviderFlavor> _byKey;

    public LoginProviderFlavorRegistry(IEnumerable<ILoginProviderFlavor> flavors)
    {
        _byKey = flavors.ToDictionary(f => f.Key, f => f, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<ILoginProviderFlavor> All => _byKey.Values;

    public bool TryGet(string key, out ILoginProviderFlavor flavor)
        => _byKey.TryGetValue(key, out flavor!);

    public ILoginProviderFlavor Get(string key)
    {
        if (!_byKey.TryGetValue(key, out var flavor))
            throw new KeyNotFoundException(
                $"No login provider flavor registered for key '{key}'. Known: {string.Join(", ", _byKey.Keys)}.");
        return flavor;
    }
}
