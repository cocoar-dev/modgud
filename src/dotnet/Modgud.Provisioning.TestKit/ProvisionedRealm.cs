namespace Modgud.Provisioning.TestKit;

/// <summary>
/// A live, isolated realm provisioned by <see cref="ModgudProvisioningClient.ImportRealmAsync"/>.
/// Exposes everything a consumer-app test needs to point its OAuth/OIDC client at the realm —
/// the <see cref="Authority"/>, the configured client ids, and their freshly minted secrets.
/// Disposing the handle HARD-deletes the realm (drops the tenant database), so a
/// <c>await using</c> gives each test a throwaway realm with automatic teardown.
/// </summary>
public sealed class ProvisionedRealm : IAsyncDisposable
{
    private readonly ModgudProvisioningClient _client;
    private bool _deleted;

    internal ProvisionedRealm(ModgudProvisioningClient client, RealmImportResult result)
    {
        _client = client;
        Slug = result.Slug;
        PrimaryDomain = result.PrimaryDomain;
        ClientSecrets = result.ClientSecrets;
    }

    /// <summary>The realm's slug — its identity in the control-plane API.</summary>
    public string Slug { get; }

    /// <summary>The realm's canonical public host (one of its domains). Anchors the issuer
    /// and the WebAuthn RP id.</summary>
    public string PrimaryDomain { get; }

    /// <summary>The OIDC authority / issuer base URL for this realm
    /// (<c>https://{PrimaryDomain}</c>) — feed this to the app-under-test's OIDC handler.</summary>
    public string Authority => $"https://{PrimaryDomain}";

    /// <summary>Plaintext secrets of the confidential clients created with the realm,
    /// keyed by client id. Only available here (the server never returns them again).</summary>
    public IReadOnlyDictionary<string, string> ClientSecrets { get; }

    /// <summary>The plaintext secret for <paramref name="clientId"/>, or throws if the client
    /// was not a confidential client created with this realm.</summary>
    public string SecretFor(string clientId)
        => ClientSecrets.TryGetValue(clientId, out var secret)
            ? secret
            : throw new KeyNotFoundException(
                $"No client secret for '{clientId}' in realm '{Slug}'. Known clients: {string.Join(", ", ClientSecrets.Keys)}.");

    /// <summary>Applies <paramref name="manifest"/> to this realm in place (merge/upsert).
    /// The manifest's realm slug must match this realm. New confidential-client secrets are
    /// NOT surfaced — existing clients keep their secret.</summary>
    public Task ApplyAsync(RealmManifest manifest, CancellationToken ct = default)
    {
        if (!string.Equals(manifest.Realm.Slug, Slug, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Manifest realm slug '{manifest.Realm.Slug}' does not match this realm '{Slug}'.", nameof(manifest));
        return _client.ApplyAsync(Slug, manifest, ct);
    }

    /// <summary>Hard-deletes the realm (drops the tenant database). Idempotent — a second call
    /// is a no-op. Called automatically by <see cref="DisposeAsync"/>.</summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        if (_deleted) return;
        await _client.HardDeleteAsync(Slug, ct);
        _deleted = true;
    }

    /// <summary>Tears the realm down via <see cref="DeleteAsync"/>. Deliberately swallows
    /// teardown failures so a cleanup error can't mask the actual test result — call
    /// <see cref="DeleteAsync"/> explicitly when you want to assert the teardown.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await DeleteAsync(); }
        catch (ModgudProvisioningException) { /* best-effort teardown */ }
    }
}
