using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Tests.Infrastructure;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class SeedDataTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;

	public SeedDataTests(SharedPostgresFixture fixture)
	{
		_factory = new CocoarAuthWebApplicationFactory(fixture);
		// Force the host to start by creating a client (triggers host initialization and seed data)
		_ = _factory.CreateClientWithCookies();
	}

	public Task InitializeAsync() => _factory.CleanDatabaseAsync();

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
	}

	[Fact]
	public async Task InternalLoginProvider_IsSeeded()
	{
		// The host startup should seed the Internal login provider.
		// Trigger seeding by calling the seed extension methods.
		await Cocoar.Auth.Infrastructure.Repositories.LoginProviderExtensions
			.SeedLoginProvidersAsync(_factory.Services);

		// Act
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
		await using var session = store.QuerySession();

		var internalProvider = await session.Query<LoginProviderState>()
			.FirstOrDefaultAsync(x => x.Name == "Internal" && !x.IsDeleted);

		// Assert
		Assert.NotNull(internalProvider);
		Assert.Equal("Internal", internalProvider.Name);
		Assert.True(internalProvider.IsBuiltIn);
	}

	[Theory]
	[InlineData("openid", "OpenID")]
	[InlineData("profile", "Profile")]
	[InlineData("email", "Email")]
	[InlineData("roles", "Roles")]
	[InlineData("offline_access", "Offline Access")]
	public async Task OpenIddictScopes_AreSeeded(string scopeName, string expectedDisplayName)
	{
		// Seed the OpenIddict scopes
		await _factory.SeedOpenIddictScopesAsync();

		// Act
		using var scope = _factory.Services.CreateScope();
		var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

		var oidcScope = await scopeManager.FindByNameAsync(scopeName);

		// Assert
		Assert.NotNull(oidcScope);
		var displayName = await scopeManager.GetDisplayNameAsync(oidcScope);
		Assert.Equal(expectedDisplayName, displayName);
	}
}
