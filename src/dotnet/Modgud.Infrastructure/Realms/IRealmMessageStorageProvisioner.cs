using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// Provisions Wolverine's transactional inbox/outbox storage for a realm
/// database that was registered with Marten after the application started.
/// </summary>
public interface IRealmMessageStorageProvisioner
{
    Task EnsureProvisionedAsync(string realmSlug);
}

internal sealed class RealmMessageStorageProvisioner : IRealmMessageStorageProvisioner
{
    private readonly IWolverineRuntime _runtime;

    public RealmMessageStorageProvisioner(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task EnsureProvisionedAsync(string realmSlug)
    {
        if (_runtime.Stores.Main is not MultiTenantedMessageStore messageStore)
        {
            throw new InvalidOperationException(
                "Wolverine's main message store is not configured for database-per-realm tenancy.");
        }

        // GetDatabaseAsync discovers a Marten tenant registered at runtime and,
        // when Wolverine auto-create is enabled, migrates its inbox/outbox tables.
        await messageStore.GetDatabaseAsync(realmSlug);
    }
}
