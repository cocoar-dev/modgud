namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Runtime lookup for DI-registered <see cref="ISamlFlavor"/> implementations.
/// Mirror of <see cref="LoginProviderFlavorRegistry"/> for the OIDC side.
/// Two parallel registries instead of one unified keeps the per-protocol
/// type-shape intact (OIDC needs <c>OidcEndpoints</c>, SAML needs
/// <see cref="SamlFlavorData"/>) without forcing <c>object</c>-typed
/// fallbacks at consumption sites.
/// </summary>
public class SamlFlavorRegistry
{
    private readonly Dictionary<string, ISamlFlavor> _byKey;

    public SamlFlavorRegistry(IEnumerable<ISamlFlavor> flavors)
    {
        _byKey = flavors.ToDictionary(f => f.Key, f => f, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<ISamlFlavor> All => _byKey.Values;

    public bool TryGet(string key, out ISamlFlavor flavor)
        => _byKey.TryGetValue(key, out flavor!);

    public ISamlFlavor Get(string key)
    {
        if (!_byKey.TryGetValue(key, out var flavor))
            throw new KeyNotFoundException(
                $"No SAML flavor registered for key '{key}'. Known: {string.Join(", ", _byKey.Keys)}.");
        return flavor;
    }
}
