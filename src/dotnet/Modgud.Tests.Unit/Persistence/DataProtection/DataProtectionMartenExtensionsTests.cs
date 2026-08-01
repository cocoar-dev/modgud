using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
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
}
