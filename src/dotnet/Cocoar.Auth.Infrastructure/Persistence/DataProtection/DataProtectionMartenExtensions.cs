using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Infrastructure.Persistence.DataProtection;

public static class DataProtectionMartenExtensions
{
    /// <summary>
    /// Wires per-tenant ASP.NET Core DataProtection — each realm gets its
    /// own cryptographically-isolated key pool stored in that realm's own
    /// database. Aligns with the per-realm signing-key model used by the
    /// rest of the IdP. See the deployment-hygiene section of
    /// <c>website/dev-notes/future-features/ha-multi-instance.md</c>
    /// (HA-2a) for the threat model.
    ///
    /// <para>Replaces the default <see cref="IDataProtectionProvider"/>
    /// registration. ASP.NET Identity / Antiforgery / Session pick up the
    /// tenanted provider via DI without any further wiring on their side
    /// — the tenant gets resolved at <c>Protect</c>/<c>Unprotect</c> time
    /// from the request's <see cref="Tenancy.TenantContext"/>.</para>
    /// </summary>
    public static IServiceCollection AddTenantedDataProtection(this IServiceCollection services)
    {
        services.AddSingleton<TenantedDataProtectionProvider>(sp =>
        {
            var store = sp.GetRequiredService<IDocumentStore>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return new TenantedDataProtectionProvider(tenantId =>
            {
                // One mini-DI per tenant, lifetime bound to the outer
                // singleton. Bounded by realm count so memory isn't a
                // concern; first request per tenant is slightly slower
                // (one-time provider build).
                var inner = new ServiceCollection();
                inner.AddSingleton(loggerFactory);
                inner.AddLogging();
                inner
                    .AddDataProtection()
                    // Defense-in-depth: even if storage isolation were
                    // bypassed accidentally, ApplicationName-prefixed
                    // payloads from one tenant wouldn't decrypt with
                    // another tenant's keys.
                    .SetApplicationName($"Cocoar.Auth-{tenantId}");

                inner.Configure<KeyManagementOptions>(opts =>
                {
                    opts.XmlRepository = new MartenXmlRepository(store, tenantId);
                });

                return inner.BuildServiceProvider()
                    .GetRequiredService<IDataProtectionProvider>();
            });
        });

        services.AddSingleton<IDataProtectionProvider>(sp =>
            sp.GetRequiredService<TenantedDataProtectionProvider>());

        return services;
    }
}
