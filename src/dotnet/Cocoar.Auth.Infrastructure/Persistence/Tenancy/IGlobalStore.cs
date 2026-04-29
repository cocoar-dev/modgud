using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Marker interface for the global (non-tenanted) Marten <see cref="IDocumentStore"/>.
/// Stores cross-tenant documents (currently <c>Realm</c>) in the master database.
/// Registered via <c>services.AddMartenStore&lt;IGlobalStore&gt;(...)</c>.
/// </summary>
public interface IGlobalStore : IDocumentStore;
