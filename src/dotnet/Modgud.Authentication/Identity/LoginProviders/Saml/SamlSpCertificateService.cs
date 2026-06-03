using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Marten;
using Modgud.Authentication.Domain.Saml;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Per-realm SAML SP certificate lifecycle: lazy generation on first use,
/// retrieval of the active cert for AuthnRequest signing / Response
/// decryption, and rotation with a 30-day metadata overlap window.
/// <para>
/// Scoped because it depends on <see cref="IDocumentSession"/> (request-scope
/// in HTTP context, scope-factory-managed in background services /
/// bootstrap). The cert document is tenant-scoped via the ambient
/// <see cref="TenantContext"/> — every call here must run inside an active
/// realm context (RealmMiddleware does that automatically for HTTP requests).
/// </para>
/// </summary>
public class SamlSpCertificateService
{
    /// <summary>
    /// Length of the metadata advertise-both overlap after a rotate. IdPs
    /// typically refresh metadata every 24 hours; 30 days is two orders of
    /// magnitude of safety on top.
    /// </summary>
    public static readonly TimeSpan RotationOverlap = TimeSpan.FromDays(30);

    /// <summary>Default validity period of a newly-generated SP cert (2 years).</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(365 * 2);

    /// <summary>Subject CN prefix; full subject is <c>CN={Prefix}-{realm-slug}</c>.</summary>
    public const string SubjectCnPrefix = "modgud-sp";

    private readonly IDocumentSession _session;
    private readonly SamlSpCertificateStore _store;
    private readonly TimeProvider _clock;
    private readonly ISecurityAuditLog _securityAudit;
    private readonly ILogger<SamlSpCertificateService> _logger;

    public SamlSpCertificateService(
        IDocumentSession session,
        SamlSpCertificateStore store,
        TimeProvider clock,
        ISecurityAuditLog securityAudit,
        ILogger<SamlSpCertificateService> logger)
    {
        _session = session;
        _store = store;
        _clock = clock;
        _securityAudit = securityAudit;
        _logger = logger;
    }

    /// <summary>
    /// Returns the active SP cert for the ambient realm. Lazily generates +
    /// persists a fresh self-signed cert if the realm has never had one.
    /// The returned <see cref="X509Certificate2"/> includes the private key
    /// for signing operations.
    /// </summary>
    public async Task<X509Certificate2> GetActiveAsync(CancellationToken ct = default)
    {
        var doc = await LoadOrCreateAsync(ct);

        var pfx = _store.TryDecrypt(doc.ActiveCertPfxEncrypted)
            ?? throw new InvalidOperationException(
                "SAML SP active cert PFX is empty or could not be decrypted — " +
                "the DataProtection key store may have rotated. Rotate the SP " +
                "cert via the admin endpoint to recover.");

        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Decryption certs (WITH private key) — active first, then the previous
    /// one if still within the rotation overlap window. The plural surface
    /// lets ITfoxtec try each in turn against an encrypted assertion, which
    /// is the only way an IdP that hasn't yet refreshed metadata (and so is
    /// still encrypting to the old SP public key) can succeed during the
    /// overlap window. Caller is responsible for disposing the returned
    /// instances (they hold native key handles).
    /// </summary>
    public async Task<IReadOnlyList<X509Certificate2>> GetDecryptionCertsAsync(CancellationToken ct = default)
    {
        var doc = await LoadOrCreateAsync(ct);
        var results = new List<X509Certificate2>(capacity: 2);

        var activePfx = _store.TryDecrypt(doc.ActiveCertPfxEncrypted);
        if (activePfx is not null)
        {
            var active = X509CertificateLoader.LoadPkcs12(activePfx, password: null, X509KeyStorageFlags.Exportable);
            results.Add(active);
        }

        var now = _clock.GetUtcNow();
        if (doc.PreviousCertPfxEncrypted is { Length: > 0 }
            && doc.PreviousRetiresAt is { } retiresAt
            && retiresAt > now)
        {
            var prevPfx = _store.TryDecrypt(doc.PreviousCertPfxEncrypted);
            if (prevPfx is not null)
            {
                var prev = X509CertificateLoader.LoadPkcs12(prevPfx, password: null, X509KeyStorageFlags.Exportable);
                results.Add(prev);
            }
        }

        return results;
    }

    /// <summary>
    /// All certs to advertise in SP metadata XML — active first, then the
    /// previous one if still within the rotation overlap window. Certs
    /// returned here are <b>without private key</b> — metadata only carries
    /// the public cert.
    /// </summary>
    public async Task<IReadOnlyList<X509Certificate2>> GetMetadataCertsAsync(CancellationToken ct = default)
    {
        var doc = await LoadOrCreateAsync(ct);
        var results = new List<X509Certificate2>(capacity: 2);

        var activePfx = _store.TryDecrypt(doc.ActiveCertPfxEncrypted);
        if (activePfx is not null)
        {
            var active = X509CertificateLoader.LoadPkcs12(activePfx, password: null);
            results.Add(active);
        }

        var now = _clock.GetUtcNow();
        if (doc.PreviousCertPfxEncrypted is { Length: > 0 }
            && doc.PreviousRetiresAt is { } retiresAt
            && retiresAt > now)
        {
            var prevPfx = _store.TryDecrypt(doc.PreviousCertPfxEncrypted);
            if (prevPfx is not null)
            {
                var prev = X509CertificateLoader.LoadPkcs12(prevPfx, password: null);
                results.Add(prev);
            }
        }

        return results;
    }

    /// <summary>
    /// Rotates the active SP cert: generates a fresh cert, moves the
    /// current Active → Previous with <see cref="RotationOverlap"/> until
    /// retire date, installs the new cert as Active. Idempotent in the
    /// sense that calling twice yields the latest active cert; the older
    /// Previous gets pushed out, not stacked indefinitely.
    /// </summary>
    public async Task<X509Certificate2> RotateAsync(CancellationToken ct = default)
    {
        var doc = await _session.LoadAsync<SamlSpCertificateDocument>(
            SamlSpCertificateDocument.SingletonId, ct);

        var realmSlug = TenantContext.CurrentOrNull
            ?? throw new InvalidOperationException(
                "SamlSpCertificateService requires an ambient TenantContext.");

        // Cooldown: refuse a second rotation while the previous cert is still
        // in the overlap window. The second rotation would overwrite that
        // previous slot — the cert IdPs are most likely still cached on —
        // and break AuthnRequest signature validation for 24h+ until each
        // IdP refreshes its cached metadata. Admin must either wait for
        // RetireExpiredPreviousAsync to clear the slot or escalate via a
        // future force-rotate path.
        if (doc is not null
            && doc.PreviousCertPfxEncrypted is { Length: > 0 }
            && doc.PreviousRetiresAt is { } stillInOverlap
            && stillInOverlap > _clock.GetUtcNow())
        {
            throw new InvalidOperationException(
                $"SAML SP cert rotation refused for realm {realmSlug}: a previous " +
                $"rotation is still in the overlap window until {stillInOverlap:o}. " +
                "Wait until then before rotating again.");
        }

        var (newCert, newPfxEncrypted) = GenerateAndEncrypt(realmSlug);

        if (doc is null)
        {
            doc = new SamlSpCertificateDocument
            {
                ActiveCertPfxEncrypted = newPfxEncrypted,
                ActiveCertThumbprint = newCert.Thumbprint,
                ActiveCertNotBefore = newCert.NotBefore,
                ActiveCertNotAfter = newCert.NotAfter,
                ActiveCertCreatedAt = _clock.GetUtcNow(),
            };
        }
        else
        {
            // Promote existing active to previous (if any), drop pre-existing
            // previous entirely — we don't stack more than one rollover deep.
            doc.PreviousCertPfxEncrypted = doc.ActiveCertPfxEncrypted is { Length: > 0 }
                ? doc.ActiveCertPfxEncrypted : null;
            doc.PreviousCertThumbprint = string.IsNullOrEmpty(doc.ActiveCertThumbprint)
                ? null : doc.ActiveCertThumbprint;
            doc.PreviousCertNotAfter = doc.ActiveCertNotAfter == default
                ? null : doc.ActiveCertNotAfter;
            doc.PreviousRetiresAt = doc.PreviousCertPfxEncrypted is null
                ? null : _clock.GetUtcNow().Add(RotationOverlap);

            doc.ActiveCertPfxEncrypted = newPfxEncrypted;
            doc.ActiveCertThumbprint = newCert.Thumbprint;
            doc.ActiveCertNotBefore = newCert.NotBefore;
            doc.ActiveCertNotAfter = newCert.NotAfter;
            doc.ActiveCertCreatedAt = _clock.GetUtcNow();
        }

        _session.Store(doc);
        await _session.SaveChangesAsync(ct);

        _securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.SamlCertRotated,
            Realm = realmSlug,
            Level = "Info",
            Status = "rotated",
            Reason = $"thumbprint {doc.ActiveCertThumbprint}, notAfter {doc.ActiveCertNotAfter:o}",
            Message = $"Rotated SAML SP cert — new thumbprint {doc.ActiveCertThumbprint}, valid until {doc.ActiveCertNotAfter:o}",
        });

        return newCert;
    }

    /// <summary>
    /// Retires any in-overlap previous cert whose retire date has passed.
    /// Safe to call repeatedly — no-op when nothing is due to retire.
    /// </summary>
    public async Task<bool> RetireExpiredPreviousAsync(CancellationToken ct = default)
    {
        var doc = await _session.LoadAsync<SamlSpCertificateDocument>(
            SamlSpCertificateDocument.SingletonId, ct);

        if (doc is null) return false;
        if (doc.PreviousCertPfxEncrypted is not { Length: > 0 }) return false;
        if (doc.PreviousRetiresAt is null || doc.PreviousRetiresAt > _clock.GetUtcNow()) return false;

        var oldThumb = doc.PreviousCertThumbprint;
        doc.PreviousCertPfxEncrypted = null;
        doc.PreviousCertThumbprint = null;
        doc.PreviousCertNotAfter = null;
        doc.PreviousRetiresAt = null;

        _session.Store(doc);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Retired previous SAML SP cert (thumbprint {Thumbprint})", oldThumb);

        return true;
    }

    private async Task<SamlSpCertificateDocument> LoadOrCreateAsync(CancellationToken ct)
    {
        var doc = await _session.LoadAsync<SamlSpCertificateDocument>(
            SamlSpCertificateDocument.SingletonId, ct);

        if (doc is not null && doc.ActiveCertPfxEncrypted.Length > 0)
            return doc;

        // First-use generation. Locking is unnecessary because Marten's
        // upsert is last-write-wins on Identity-keyed docs; a race between
        // two concurrent first-uses would result in two generated certs of
        // which only one ends up stored — neither is a security incident
        // (both are valid self-signed certs, the loser is just thrown away).
        var realmSlug = TenantContext.CurrentOrNull
            ?? throw new InvalidOperationException(
                "SamlSpCertificateService requires an ambient TenantContext.");

        var (cert, pfxEncrypted) = GenerateAndEncrypt(realmSlug);

        doc = new SamlSpCertificateDocument
        {
            ActiveCertPfxEncrypted = pfxEncrypted,
            ActiveCertThumbprint = cert.Thumbprint,
            ActiveCertNotBefore = cert.NotBefore,
            ActiveCertNotAfter = cert.NotAfter,
            ActiveCertCreatedAt = _clock.GetUtcNow(),
        };

        _session.Store(doc);
        await _session.SaveChangesAsync(ct);

        _securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.SamlCertRotated,
            Realm = realmSlug,
            Level = "Info",
            Status = "generated",
            Reason = $"initial cert, thumbprint {doc.ActiveCertThumbprint}",
            Message = $"Generated initial SAML SP cert — thumbprint {doc.ActiveCertThumbprint}, valid until {doc.ActiveCertNotAfter:o}",
        });

        return doc;
    }

    private (X509Certificate2 Cert, byte[] PfxEncrypted) GenerateAndEncrypt(string realmSlug)
    {
        using var rsa = RSA.Create(2048);
        var subject = $"CN={SubjectCnPrefix}-{Sanitize(realmSlug)}";
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [
                new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth
                new Oid("1.3.6.1.5.5.7.3.2"), // clientAuth
            ],
            critical: false));

        // Subject Alternative Name with the realm slug + a generic id so SAML
        // implementations that read SANs (some IdPs prefer this over CN) get
        // a sane value.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName($"{Sanitize(realmSlug)}.modgud.local");
        req.CertificateExtensions.Add(sanBuilder.Build());

        var now = _clock.GetUtcNow();
        // -5min so a clock skew on the IdP side doesn't briefly see a
        // not-yet-valid cert just after we generated it.
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.Add(DefaultValidity);

        using var newCert = req.CreateSelfSigned(notBefore, notAfter);
        var pfxBytes = newCert.Export(X509ContentType.Pkcs12);
        var encrypted = _store.Encrypt(pfxBytes);

        // Re-load from the PFX bytes so the returned cert is independent of
        // the disposable `using` above (and is exportable, which CreateSelfSigned-
        // returned certs aren't always cleanly).
        var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);

        return (cert, encrypted);
    }

    private static string Sanitize(string realmSlug) =>
        // Subject CNs and SAN DNS names can't have a few characters cleanly;
        // realm slugs are already lowercase alphanumeric + hyphen by
        // convention but defend anyway.
        new string(realmSlug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}
