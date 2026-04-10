using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence;

/// <summary>
/// Marker interface for the global (non-tenanted) Marten DocumentStore.
/// Stores cross-tenant documents like Realm in the shared database.
/// Registered via AddMartenStore&lt;IGlobalStore&gt;.
/// </summary>
public interface IGlobalStore : IDocumentStore;
