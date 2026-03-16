using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Infrastructure.Repositories;

/// <summary>
/// Extension methods for seeding login provider data.
/// </summary>
public static class LoginProviderExtensions
{
	/// <summary>
	/// Seeds the built-in "Internal" login provider if it doesn't exist (system realm).
	/// </summary>
	public static async Task SeedLoginProvidersAsync(this IServiceProvider serviceProvider)
	{
		using var scope = serviceProvider.CreateScope();
		var repository = scope.ServiceProvider.GetRequiredService<ILoginProviderRepository>();
		await repository.EnsureInternalProviderExistsAsync();
	}

	/// <summary>
	/// Seeds the built-in "Internal" login provider for a specific tenant realm.
	/// Uses the document store directly to target the correct tenant database.
	/// </summary>
	public static async Task SeedLoginProvidersAsync(this IServiceProvider serviceProvider, string tenantId)
	{
		using var scope = serviceProvider.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
		await using var session = store.LightweightSession(tenantId);

		var existing = await session.Query<LoginProviderState>()
			.FirstOrDefaultAsync(x => x.Name == "Internal" && !x.IsDeleted);

		if (existing is not null)
		{
			return;
		}

		var id = Guid.NewGuid();

		var (_, createdEvent) = LoginProviderAggregate.Create(
			id,
			"Internal",
			"Internal Authentication",
			"Built-in password-based authentication",
			LoginProviderType.Internal,
			new Dictionary<string, string>(),
			isBuiltIn: true);

		session.Events.StartStream<LoginProviderAggregate>(id, createdEvent);
		await session.SaveChangesAsync();
	}
}
