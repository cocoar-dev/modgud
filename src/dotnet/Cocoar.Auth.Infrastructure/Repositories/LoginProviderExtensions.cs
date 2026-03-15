using Cocoar.Auth.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Infrastructure.Repositories;

/// <summary>
/// Extension methods for seeding login provider data.
/// </summary>
public static class LoginProviderExtensions
{
	/// <summary>
	/// Seeds the built-in "Internal" login provider if it doesn't exist.
	/// </summary>
	public static async Task SeedLoginProvidersAsync(this IServiceProvider serviceProvider)
	{
		using var scope = serviceProvider.CreateScope();
		var repository = scope.ServiceProvider.GetRequiredService<ILoginProviderRepository>();
		await repository.EnsureInternalProviderExistsAsync();
	}
}
