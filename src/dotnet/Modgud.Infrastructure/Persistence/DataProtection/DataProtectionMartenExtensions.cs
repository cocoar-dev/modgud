using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // AddSession/AddAntiforgery register ASP.NET Core's default key manager
        // before this extension replaces IDataProtectionProvider. Its hosted
        // service would otherwise create a second, unused key ring under
        // ~/.aspnet/DataProtection-Keys and emit a misleading container-
        // persistence warning. Keep that root key manager deliberately inert;
        // the per-tenant mini-containers below have independent options and
        // continue to use MartenXmlRepository with normal key generation.
        services.Configure<KeyManagementOptions>(options =>
        {
            options.AutoGenerateKeys = false;
            options.XmlRepository = DisabledRootXmlRepository.Instance;
        });

        services.RemoveRootKeyRingStartupCheck();

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

    /// <summary>
    /// Removes ASP.NET Core's <c>DataProtectionHostedService</c>, the startup
    /// self-check belonging to the root key manager that
    /// <see cref="AddTenantedDataProtection"/> deliberately renders inert.
    ///
    /// <para>That hosted service resolves the root key ring on <c>StartAsync</c>
    /// purely to surface problems early. Against an inert manager the read can
    /// never succeed, so every cold start logged two ERROR lines — "The key ring
    /// does not contain a valid default protection key" from
    /// <c>KeyRingProvider</c>, then "An error occurred while reading the key
    /// ring" — describing a state we chose on purpose. For an IdP that is
    /// expensive noise: an operator must be able to trust that an error at boot
    /// means something is genuinely broken.</para>
    ///
    /// <para><b>Call this LAST, after every other registration.</b> The
    /// registration uses <c>TryAddEnumerable</c>, so any later
    /// <c>AddDataProtection()</c> puts it back — OpenIddict's builder does
    /// exactly that. Removing it only from inside
    /// <see cref="AddTenantedDataProtection"/> is therefore not enough whenever
    /// OpenIddict is registered afterwards. The call is idempotent.</para>
    ///
    /// <para>Only this one registration is touched. The per-tenant providers
    /// build their own containers with their own <c>KeyManagementOptions</c> and
    /// keep normal key generation against <c>MartenXmlRepository</c>, so a real
    /// per-realm key-ring failure still surfaces.</para>
    /// </summary>
    public static IServiceCollection RemoveRootKeyRingStartupCheck(this IServiceCollection services)
    {
        var startupCheck = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType is { Name: "DataProtectionHostedService" }
            && descriptor.ImplementationType.Assembly == typeof(KeyManagementOptions).Assembly);

        if (startupCheck is not null)
            services.Remove(startupCheck);

        return services;
    }

    private sealed class DisabledRootXmlRepository : IXmlRepository
    {
        public static readonly DisabledRootXmlRepository Instance = new();

        public IReadOnlyCollection<XElement> GetAllElements() => [];

        public void StoreElement(XElement element, string friendlyName)
        {
            // Intentionally ignored. The root provider is replaced by
            // TenantedDataProtectionProvider and must never persist keys.
        }
    }
}
