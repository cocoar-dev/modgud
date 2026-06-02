using System.Collections.Concurrent;
using System.Security.Cryptography;
using Modgud.Domain.Realms;
using Marten;
using Microsoft.IdentityModel.Tokens;

namespace Modgud.Infrastructure.Realms;

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
/// are opened with explicit slug arguments rather than relying on the ambient
/// <c>TenantContext</c>: bootstrap may be called from a different realm's
/// request scope (e.g. an admin in the system realm provisioning a tenant).
/// </para>
///
/// <para>
/// Rotation overlap: on <see cref="RotateAsync"/> the previous active key is
/// flagged retired (with a <see cref="RealmSigningKey.RetiredAt"/> stamp) but
/// kept in the verification set + JWKS for <see cref="RotationOverlap"/> so
/// tokens issued just before the rotation stay validatable.
/// <see cref="PurgeExpiredRetiredKeysAsync"/> (driven by the
/// <c>SigningKeyJanitorJob</c>) hard-deletes keys whose overlap has elapsed.
/// </para>
///
/// <para>
/// Staleness bound (multi-instance / out-of-band rotation): the caches are
/// process-local, so a rotation performed by another instance — or by the
/// recovery CLI, which is a separate process — is not seen until this process's
/// cache is re-read. Both caches therefore carry a freshness deadline:
/// <see cref="_activeCache"/> entries re-validate the active row against the DB
/// after <see cref="CacheRevalidateInterval"/>, and the verification set's
/// <c>ValidUntil</c> is capped at <c>now + CacheRevalidateInterval</c>. That
/// bounds the window in which a peer keeps signing with a just-retired key to
/// seconds (well inside the 30-day overlap), instead of forever. Cross-node
/// push invalidation (LISTEN/NOTIFY) is a future HA improvement.
/// </para>
/// </summary>
public sealed class RealmKeyStore : IRealmKeyStore
{
    /// <summary>
    /// How long a retired key stays trusted for verification (and listed in
    /// the JWKS) after a rotation. Mirrors the SAML SP certificate overlap —
    /// IdPs / resource servers refresh JWKS far more often than this, so 30
    /// days is two orders of magnitude of safety margin.
    /// </summary>
    public static readonly TimeSpan RotationOverlap = TimeSpan.FromDays(30);

    /// <summary>
    /// Upper bound on how stale a process-local cache entry may be before it is
    /// re-read from the DB. Bounds the propagation delay of an out-of-band
    /// rotation (another instance, or the recovery CLI process) to this many
    /// seconds. Cheap: at most one indexed re-read per realm per interval.
    /// </summary>
    public static readonly TimeSpan CacheRevalidateInterval = TimeSpan.FromSeconds(60);

    private readonly IDocumentStore _store;
    private readonly TimeProvider _clock;

    // Active signing credentials per realm, with the kid they were built from
    // and when they were loaded — so a stale entry can be re-validated against
    // the DB after CacheRevalidateInterval.
    private readonly ConcurrentDictionary<string, ActiveEntry> _activeCache = new();

    // Verification keys = active + retired-still-in-overlap, paired with the
    // moment the set must be recomputed (min of the earliest retired-key
    // overlap expiry and now + CacheRevalidateInterval).
    private readonly ConcurrentDictionary<string, VerificationSet> _verificationCache = new();

    // Per-realm async lock guarding active-key creation/caching so concurrent
    // first-token-issuances against the same realm don't both generate (and
    // persist) different keys.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slugLocks = new();

    // Separate per-realm lock guarding the verification-set rebuild. Distinct
    // from _slugLocks because GetVerificationKeysAsync calls
    // GetActiveSigningCredentialsAsync (which takes _slugLocks) BEFORE rebuilding
    // — sharing one non-reentrant semaphore would self-deadlock.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _verifyLocks = new();

    // Per-realm monotonic generation, bumped whenever the verification cache is
    // invalidated (rotate / purge). A rebuild snapshots the generation before
    // reading the DB and discards its result if the generation moved meanwhile —
    // this closes a lost-update race where a rebuild that started before a
    // rotation could otherwise write its pre-rotation snapshot back AFTER the
    // rotation's invalidation, dropping the new active key for one cache cycle.
    // RotateAsync runs under _slugLocks and the rebuild under _verifyLocks, so
    // the two never mutually exclude; this counter is the cross-lock signal.
    private readonly ConcurrentDictionary<string, long> _verGen = new();

    private sealed record ActiveEntry(SigningCredentials Creds, string Kid, DateTimeOffset LoadedAt);

    private sealed record VerificationSet(IReadOnlyList<SecurityKey> Keys, DateTimeOffset ValidUntil);

    public RealmKeyStore(IDocumentStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task<SigningCredentials> GetActiveSigningCredentialsAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        var now = _clock.GetUtcNow();
        if (_activeCache.TryGetValue(realmSlug, out var entry) &&
            now - entry.LoadedAt < CacheRevalidateInterval)
        {
            return entry.Creds; // fresh fast path — no DB round-trip
        }

        var sem = _slugLocks.GetOrAdd(realmSlug, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            now = _clock.GetUtcNow();
            if (_activeCache.TryGetValue(realmSlug, out entry) &&
                now - entry.LoadedAt < CacheRevalidateInterval)
            {
                return entry.Creds;
            }

            // Cold OR past the freshness deadline — reconcile against the DB so
            // an out-of-band rotation (peer instance / CLI process) is picked up.
            var active = await LoadActiveAsync(realmSlug, ct)
                         ?? await CreateAndPersistAsync(realmSlug, ct);

            if (entry is not null && entry.Kid == active.KeyId)
            {
                // unchanged — keep the existing creds (and their live RSA, which
                // may be in flight in the signing pipeline) and just refresh the
                // freshness stamp.
                var refreshed = entry with { LoadedAt = now };
                _activeCache[realmSlug] = refreshed;
                return refreshed.Creds;
            }

            return CacheActive(realmSlug, active, now).Creds;
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

        var now = _clock.GetUtcNow();
        if (_verificationCache.TryGetValue(realmSlug, out var cached) && now < cached.ValidUntil)
            return cached.Keys;

        // Ensure an active key exists (bootstrap on first use). We deliberately
        // do NOT trust its returned key for the set — the rebuild below reads
        // the active row from the DB authoritatively, so a rotation racing this
        // call cannot drop the new active key from the set.
        await GetActiveSigningCredentialsAsync(realmSlug, ct);

        var vsem = _verifyLocks.GetOrAdd(realmSlug, _ => new SemaphoreSlim(1, 1));
        await vsem.WaitAsync(ct);
        try
        {
            now = _clock.GetUtcNow();
            if (_verificationCache.TryGetValue(realmSlug, out cached) && now < cached.ValidUntil)
                return cached.Keys;

            // Snapshot the generation BEFORE reading the DB. If a rotation/purge
            // invalidates the cache while we build, the generation moves and we
            // discard our (now possibly pre-rotation) set so the next read
            // rebuilds — closing the rebuild-vs-rotate lost-update race.
            var gen = _verGen.TryGetValue(realmSlug, out var g) ? g : 0L;
            var set = await BuildVerificationSetAsync(realmSlug, now, ct);
            _verificationCache[realmSlug] = set;
            if ((_verGen.TryGetValue(realmSlug, out var g2) ? g2 : 0L) != gen)
                _verificationCache.TryRemove(realmSlug, out _); // stale build raced a rotation — drop it
            return set.Keys;
        }
        finally
        {
            vsem.Release();
        }
    }

    // Bumps the per-realm generation BEFORE dropping the cached set, so a
    // concurrent rebuild that already stored a pre-invalidation snapshot will
    // see the moved generation on its post-store check and evict its own entry.
    private void InvalidateVerificationCache(string realmSlug)
    {
        _verGen.AddOrUpdate(realmSlug, 1, (_, v) => v + 1);
        _verificationCache.TryRemove(realmSlug, out _);
    }

    public async Task<SigningCredentials> RotateAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        var sem = _slugLocks.GetOrAdd(realmSlug, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            await using var session = _store.LightweightSession(realmSlug);

            // Retire EVERY currently-active key (normally exactly one; retiring
            // all self-heals a stray multi-active state from a prior cross-process
            // race). Each is kept for the overlap window so already-issued tokens
            // can still be verified.
            var actives = await session.Query<RealmSigningKey>()
                .Where(k => k.IsActive)
                .ToListAsync(ct);
            foreach (var current in actives)
            {
                current.IsActive = false;
                current.RetiredAt = now;
                session.Store(current);
            }

            // Generate fresh key and persist.
            var fresh = CreateNewKeyDocument(realmSlug);
            session.Store(fresh);
            await session.SaveChangesAsync(ct);

            // Cache the fresh key as the active credentials DIRECTLY. Do NOT
            // call GetActiveSigningCredentialsAsync here — it re-acquires `sem`
            // (a non-reentrant SemaphoreSlim we already hold) and would deadlock.
            var entry = CacheActive(realmSlug, fresh, now);

            // Invalidate (and bump the generation) AFTER the active cache is
            // updated, so a concurrent verification rebuild either rebuilds with
            // the new active key or discards its stale snapshot via the gen check.
            InvalidateVerificationCache(realmSlug);

            return entry.Creds;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<int> PurgeExpiredRetiredKeysAsync(
        string realmSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmSlug);

        var cutoff = _clock.GetUtcNow() - RotationOverlap;

        await using var session = _store.LightweightSession(realmSlug);
        var expired = await session.Query<RealmSigningKey>()
            .Where(k => !k.IsActive && k.RetiredAt != null && k.RetiredAt < cutoff)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return 0;

        foreach (var key in expired)
            session.Delete(key);
        await session.SaveChangesAsync(ct);

        // Drop the cached verification set so it rebuilds without the purged
        // keys. (The ValidUntil expiry would have evicted them anyway; this
        // makes the DB and the in-memory set converge immediately.)
        InvalidateVerificationCache(realmSlug);

        return expired.Count;
    }

    private async Task<RealmSigningKey?> LoadActiveAsync(string realmSlug, CancellationToken ct)
    {
        await using var session = _store.QuerySession(realmSlug);
        // Deterministic pick if a stray multi-active state ever exists: newest wins.
        return await session.Query<RealmSigningKey>()
            .Where(k => k.IsActive)
            .OrderByDescending(k => k.CreatedAt)
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

    private RealmSigningKey CreateNewKeyDocument(string realmSlug)
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
            CreatedAt = _clock.GetUtcNow(),
        };
    }

    private ActiveEntry CacheActive(string realmSlug, RealmSigningKey active, DateTimeOffset now)
    {
        // Build the SigningCredentials and SecurityKey ONCE per active key —
        // they're heavy (own an RSA instance) and used on every request. The
        // RSA is intentionally NOT disposed on cache overwrite: the instance is
        // handed to OpenIddict's signing pipeline and may still be in flight, so
        // eager disposal would risk use-after-dispose. It is reclaimed by the GC
        // finalizer once no longer referenced — acceptable at manual-rotation
        // cadence (a small handful of keypairs over a process lifetime).
        var rsa = RSA.Create();
        rsa.ImportFromPem(active.PrivateKeyPem);
        var key = new RsaSecurityKey(rsa) { KeyId = active.KeyId };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        var entry = new ActiveEntry(creds, active.KeyId, now);
        _activeCache[realmSlug] = entry;
        return entry;
    }

    /// <summary>
    /// Builds the realm's verification set authoritatively from the DB: every
    /// active key plus every retired key still inside <see cref="RotationOverlap"/>,
    /// de-duplicated by KeyId, public-only (verification never needs the private
    /// half). Reading the active row here — rather than trusting a key captured
    /// by the caller — closes the READ side of the rotation-vs-rebuild race; the
    /// STORE-BACK side (a rebuild that began pre-rotation publishing its stale
    /// snapshot) is closed by the generation check in
    /// <see cref="GetVerificationKeysAsync"/>. <c>ValidUntil</c> is the earlier
    /// of the soonest overlap expiry and <c>now + CacheRevalidateInterval</c>, so
    /// the set both ages retired keys out on time AND re-reads periodically to pick
    /// up out-of-band rotations.
    /// </summary>
    private async Task<VerificationSet> BuildVerificationSetAsync(
        string realmSlug, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = _store.QuerySession(realmSlug);
        var all = await session.Query<RealmSigningKey>().ToListAsync(ct);

        var byKid = new Dictionary<string, SecurityKey>();
        var validUntil = now + CacheRevalidateInterval;

        foreach (var k in all)
        {
            if (k.IsActive)
            {
                byKid.TryAdd(k.KeyId, BuildPublicKey(k));
            }
            else if (k.RetiredAt is { } retiredAt)
            {
                var expiry = retiredAt + RotationOverlap;
                if (expiry <= now)
                    continue; // past the overlap — no longer trusted for verification
                byKid.TryAdd(k.KeyId, BuildPublicKey(k));
                if (expiry < validUntil)
                    validUntil = expiry;
            }
        }

        return new VerificationSet(byKid.Values.ToList(), validUntil);
    }

    private static RsaSecurityKey BuildPublicKey(RealmSigningKey key)
    {
        // Public-only RSA for verification; intentionally not disposed (handed to
        // the validation pipeline / JWKS rendering). GC-reclaimed like the active
        // key above.
        var rsa = RSA.Create();
        rsa.ImportFromPem(key.PublicKeyPem);
        return new RsaSecurityKey(rsa) { KeyId = key.KeyId };
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
