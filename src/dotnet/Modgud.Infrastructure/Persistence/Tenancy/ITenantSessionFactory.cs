using Marten;

namespace Modgud.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Abstraction for opening tenant-scoped Marten sessions explicitly.
/// Use this from classes that manage their own session lifecycle (background
/// services, custom Identity stores). Endpoints and Wolverine handlers should
/// inject <see cref="IDocumentSession"/> / <see cref="IQuerySession"/> directly —
/// the registered <see cref="ISessionFactory"/> resolves the tenant automatically.
/// </summary>
public interface ITenantSessionFactory
{
    IDocumentSession OpenSession();
    IQuerySession OpenQuerySession();
}
