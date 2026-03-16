using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence;

/// <summary>
/// Abstraction for creating tenant-scoped Marten sessions.
/// Used by classes that manage their own session lifecycle (OpenIddict stores, repositories).
/// </summary>
public interface ITenantSessionFactory
{
	IDocumentSession OpenSession();
	IQuerySession OpenQuerySession();
}
