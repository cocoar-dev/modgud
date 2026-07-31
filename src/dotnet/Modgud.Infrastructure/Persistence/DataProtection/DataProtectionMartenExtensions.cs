using System.Security.Cryptography.X509Certificates;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modgud.Infrastructure.Persistence.DataProtection;

public static class DataProtectionMartenExtensions
{
    /// <summary>
    /// Wires per-tenant ASP.NET Core DataProtection — each realm gets its
    /// own cryptographically-isolated key pool stored in that realm's own
    /// database. Aligns with the per-realm signing-key model used by the
    /// rest of the IdP. See the deployment-hygiene section of
    /// the maintainers' <c>ha-multi-instance</c> design note
    /// (HA-2a) for the threat model.
    ///
    /// <para>Replaces the default <see cref="IDataProtectionProvider"/>
    /// registration. ASP.NET Identity / Antiforgery / Session pick up the
    /// tenanted provider via DI without any further wiring on their side
    /// — the tenant gets resolved at <c>Protect</c>/<c>Unprotect</c> time
    /// from the request's <see cref="Tenancy.TenantContext"/>.</para>
    /// </summary>
    /// <param name="protectionCertificate">
    /// Optional operator-supplied certificate (audit M7). When provided, each
    /// realm's DataProtection key ring is encrypted at rest with it, so a
    /// tenant-DB dump exposes ciphertext rather than the keys that protect
    /// login-provider secrets, SAML SP keys, captcha secrets and auth cookies.
    /// Null = the ring stays unencrypted (the DB partition is the boundary),
    /// unchanged from before — the operator opts in by mounting a cert.
    /// </param>
    public static IServiceCollection AddTenantedDataProtection(
        this IServiceCollection services,
        X509Certificate2? protectionCertificate = null)
    {
        services.AddSingleton<TenantedDataProtectionProvider>(sp =>
        {
            var store = sp.GetRequiredService<IDocumentStore>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var httpContextAccessor = sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

            return new TenantedDataProtectionProvider(
                tenantId =>
                {
                    // One mini-DI per tenant, lifetime bound to the outer
                    // singleton. Bounded by realm count so memory isn't a
                    // concern; first request per tenant is slightly slower
                    // (one-time provider build).
                    var inner = new ServiceCollection();
                    inner.AddSingleton(loggerFactory);
                    inner.AddLogging();
                    var dpBuilder = inner
                        .AddDataProtection()
                        // Defense-in-depth: even if storage isolation were
                        // bypassed accidentally, ApplicationName-prefixed
                        // payloads from one tenant wouldn't decrypt with
                        // another tenant's keys.
                        .SetApplicationName($"Modgud-{tenantId}");

                    // Audit M7: encrypt the key ring at rest when an operator cert
                    // is configured. Mixing is safe — pre-existing unencrypted keys
                    // stay readable; only new keys are wrapped.
                    if (protectionCertificate is not null)
                        dpBuilder.ProtectKeysWithCertificate(protectionCertificate);

                    inner.Configure<KeyManagementOptions>(opts =>
                    {
                        opts.XmlRepository = new MartenXmlRepository(store, tenantId);
                    });

                    return inner.BuildServiceProvider()
                        .GetRequiredService<IDataProtectionProvider>();
                },
                httpContextAccessor);
        });

        services.AddSingleton<IDataProtectionProvider>(sp =>
            sp.GetRequiredService<TenantedDataProtectionProvider>());

        return services;
    }
}
