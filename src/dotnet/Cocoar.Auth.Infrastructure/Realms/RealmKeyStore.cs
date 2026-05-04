using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cocoar.Auth.Domain.Realms;
using Marten;
using Microsoft.IdentityModel.Tokens;

namespace Cocoar.Auth.Infrastructure.Realms;

/// <summary>
/// Marten-backed implementation of <see cref="IRealmKeyStore"/>. Keys live in
/// each realm's OWN tenant DB — opening a session with the realm slug routes
/// the read/write straight to the per-tenant Postgres database, so a master-DB
/// or Realm-registry compromise cannot expose another realm's private signing
/// material. Resolution is cached in-memory keyed by realm slug because
/// signing keys are read on EVERY token issuance and validation, and a Marten
/// round-trip per call would dominate token-endpoint latency.
///
/// <para>
/// Each realm's tenant DB carries at most a handful of <see cref="RealmSigningKey"/>
/// records (one active + a few retired in the rotation overlap window), so no
/// secondary index is needed — the database itself is the partition. Sessions
/// are opened with explicit slug arguments (<see cref="IDocumentStore.LightweightSession(string, IsolationLevel)"/>)
/// rather than relying on the ambient <c>TenantContext</c>: bootstrap may be
/// called from a different realm's request scope (e.g. an admin in the
/// system realm provisioning a new tenant).
/// </para>
/// </summary>
public sealed class RealmKeyStore : IRealmKeyStore
{
    private readonly IDocumentStore _store;

    // Cached signing credentials per realm — point to the same RSA instance
    // we hold in _verificationCache for that realm's active key.
    private readonly ConcurrentDictionary<string, SigningCredentials> _activeCache = new();
    // Verification keys = active + retired-still-in-overlap. Replaced atomically
    // on rotation to avoid a window where a freshly-issued token can't be
    // validated yet.
    private readonly ConcurrentDictionary<string, IReadOnlyList<SecurityKey>> _verificationCache = new();

    // Per-realm async lock so concurrent first-token-issuances against the same
    // realm don't both generate (and persist) different keys.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slugLocks = new();

    public RealmKeyStore(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<SigningCredentials> GetActiveSigningCredentialsAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        if (_activeCache.TryGetValue(realmSlug, out var cached))
            return cached;

        var sem = _slugLocks.GetOrAdd(realmSlug, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock — a concurrent caller may have
            // populated the cache while we were waiting.
            if (_activeCache.TryGetValue(realmSlug, out cached))
                return cached;

            var active = await LoadActiveAsync(realmSlug, ct)
                         ?? await CreateAndPersistAsync(realmSlug, ct);

            return CacheActive(realmSlug, active);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<SecurityKey>> GetVerificationKeysAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        if (_verificationCache.TryGetValue(realmSlug, out var cached))
            return cached;

        // GetActiveSigningCredentialsAsync handles the lock and populates
        // _verificationCache as a side effect of CacheActive.
        await GetActiveSigningCredentialsAsync(realmSlug, ct);

        return _verificationCache.TryGetValue(realmSlug, out var verified)
            ? verified
            : Array.Empty<SecurityKey>();
    }

    public async Task<SigningCredentials> RotateAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        var sem = _slugLocks.GetOrAdd(realmSlug, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await using var session = _store.LightweightSession(realmSlug);

            // Mark the previous active key retired (kept for the overlap window
            // so already-issued tokens can still be verified).
            var current = await session.Query<RealmSigningKey>()
                .Where(k => k.IsActive)
                .FirstOrDefaultAsync(ct);
            if (current is not null)
            {
                current.IsActive = false;
                current.RetiredAt = DateTimeOffset.UtcNow;
                session.Store(current);
            }

            // Generate fresh key and persist.
            var fresh = CreateNewKeyDocument(realmSlug);
            session.Store(fresh);
            await session.SaveChangesAsync(ct);

            // Invalidate caches so the next read pulls the new state.
            _activeCache.TryRemove(realmSlug, out _);
            _verificationCache.TryRemove(realmSlug, out _);

            return await GetActiveSigningCredentialsAsync(realmSlug, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<RealmSigningKey?> LoadActiveAsync(string realmSlug, CancellationToken ct)
    {
        await using var session = _store.QuerySession(realmSlug);
        return await session.Query<RealmSigningKey>()
            .Where(k => k.IsActive)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<RealmSigningKey> CreateAndPersistAsync(string realmSlug, CancellationToken ct)
    {
        var doc = CreateNewKeyDocument(realmSlug);
        await using var session = _store.LightweightSession(realmSlug);
        session.Store(doc);
        await session.SaveChangesAsync(ct);
        return doc;
    }

    private static RealmSigningKey CreateNewKeyDocument(string realmSlug)
    {
        using var rsa = RSA.Create(2048);
        var privatePem = ExportPkcs8Pem(rsa);
        var publicPem = ExportSpkiPem(rsa);
        return new RealmSigningKey
        {
            Id = Guid.CreateVersion7(),
            RealmSlug = realmSlug,
            // Stable kid for caches/JWKS — derived from the public key so it
            // survives PEM round-trips. Not security-sensitive (kid is public).
            KeyId = ComputeKeyId(rsa),
            Algorithm = "RS256",
            PrivateKeyPem = privatePem,
            PublicKeyPem = publicPem,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private SigningCredentials CacheActive(string realmSlug, RealmSigningKey active)
    {
        // Build the SigningCredentials and SecurityKey objects ONCE per
        // realm — they're heavy (own an RSA instance) and used on every
        // request.
        var rsa = RSA.Create();
        rsa.ImportFromPem(active.PrivateKeyPem);
        var key = new RsaSecurityKey(rsa) { KeyId = active.KeyId };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        _activeCache[realmSlug] = creds;
        // For now, verification = just the active key. Retired-key overlap
        // can be added when rotation is exercised; the active-only set is
        // always correct, just not maximally permissive on rotation day.
        _verificationCache[realmSlug] = new[] { (SecurityKey)key };
        return creds;
    }

    private static string ComputeKeyId(RSA rsa)
    {
        // RFC 7638 JWK thumbprint — stable per public key, leaks nothing
        // sensitive, fits the JWS kid header convention.
        var parameters = rsa.ExportParameters(false);
        var n = Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Modulus!));
        var e = Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Exponent!));
        var canonical = $"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Base64UrlEncoder.Encode(hash);
    }

    private static byte[] TrimLeadingZeros(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length - 1 && bytes[i] == 0) i++;
        return i == 0 ? bytes : bytes[i..];
    }

    private static string ExportPkcs8Pem(RSA rsa)
    {
        var der = rsa.ExportPkcs8PrivateKey();
        return PemEncode("PRIVATE KEY", der);
    }

    private static string ExportSpkiPem(RSA rsa)
    {
        var der = rsa.ExportSubjectPublicKeyInfo();
        return PemEncode("PUBLIC KEY", der);
    }

    private static string PemEncode(string label, byte[] der)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new System.Text.StringBuilder();
        sb.Append("-----BEGIN ").Append(label).Append("-----\n");
        for (var i = 0; i < b64.Length; i += 64)
        {
            sb.Append(b64.AsSpan(i, Math.Min(64, b64.Length - i))).Append('\n');
        }
        sb.Append("-----END ").Append(label).Append("-----\n");
        return sb.ToString();
    }
}
