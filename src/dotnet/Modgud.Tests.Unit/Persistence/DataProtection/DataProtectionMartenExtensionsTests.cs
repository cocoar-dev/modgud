using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modgud.Infrastructure.Persistence.DataProtection;

namespace Modgud.Tests.Unit.Persistence.DataProtection;

public sealed class DataProtectionMartenExtensionsTests
{
    [Fact]
    public void Root_key_manager_is_inert_after_tenanted_provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        services.AddTenantedDataProtection();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        Assert.False(options.AutoGenerateKeys);
        Assert.NotNull(options.XmlRepository);
        Assert.Empty(options.XmlRepository.GetAllElements());
    }

    [Fact]
    public void Root_key_ring_startup_check_is_removed_so_boot_logs_no_false_error()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        Assert.Contains(services, IsRootKeyRingStartupCheck);

        services.AddTenantedDataProtection();

        // The inert root manager can never load a key ring, so its startup
        // self-check would log an ERROR describing an intended state.
        Assert.DoesNotContain(services, IsRootKeyRingStartupCheck);
    }

    [Fact]
    public void A_later_AddDataProtection_puts_the_startup_check_back()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddTenantedDataProtection();

        // This is what OpenIddict's builder does, and it runs after
        // AddTenantedDataProtection in Program.cs. TryAddEnumerable dedupes on
        // (serviceType, implementationType), so having removed the descriptor
        // makes the re-add succeed.
        services.AddDataProtection();

        Assert.Contains(services, IsRootKeyRingStartupCheck);

        // Hence the second, late removal — which is why Program.cs calls it
        // again immediately before builder.Build().
        services.RemoveRootKeyRingStartupCheck();

        Assert.DoesNotContain(services, IsRootKeyRingStartupCheck);
    }

    [Fact]
    public void Removing_the_startup_check_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        services.RemoveRootKeyRingStartupCheck();
        services.RemoveRootKeyRingStartupCheck();

        Assert.DoesNotContain(services, IsRootKeyRingStartupCheck);
    }

    [Fact]
    public void Removing_the_startup_check_leaves_other_hosted_services_alone()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddHostedService<UnrelatedHostedService>();

        services.AddTenantedDataProtection();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(UnrelatedHostedService));
    }

    private static bool IsRootKeyRingStartupCheck(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType is { Name: "DataProtectionHostedService" };

    private sealed class UnrelatedHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
