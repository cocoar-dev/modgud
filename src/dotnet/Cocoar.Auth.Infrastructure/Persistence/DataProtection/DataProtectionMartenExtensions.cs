using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Infrastructure.Persistence.DataProtection;

public static class DataProtectionMartenExtensions
{
    /// <summary>
    /// Persists ASP.NET Core DataProtection keys to Marten in the system
    /// tenant. Drop-in replacement for the file-system default — keys
    /// survive a Container-Restart and (when the deployment scales) are
    /// readable from every instance.
    ///
    /// <para>See the deployment-hygiene section of
    /// <c>website/dev-notes/future-features/ha-multi-instance.md</c>
    /// (HA-2a) for the rationale.</para>
    /// </summary>
    public static IDataProtectionBuilder PersistKeysWithMarten(this IDataProtectionBuilder builder)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(opts =>
            {
                var store = sp.GetRequiredService<IDocumentStore>();
                opts.XmlRepository = new MartenXmlRepository(store);
            }));
        return builder;
    }
}
