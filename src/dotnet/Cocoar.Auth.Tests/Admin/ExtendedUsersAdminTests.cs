using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Tests.Infrastructure;
using Cocoar.Primitives;

namespace Cocoar.Auth.Tests.Admin;

[Collection(IntegrationTestCollection.Name)]
public class ExtendedUsersAdminTests : IAsyncLifetime
{
	private readonly CocoarAuthWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public ExtendedUsersAdminTests(SharedPostgresFixture fixture)
	{
		_factory = new CocoarAuthWebApplicationFactory(fixture);
		_client = _factory.CreateClientWithCookies();
	}

	public Task InitializeAsync() => _factory.CleanDatabaseAsync();

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task LoginAsAdminAsync()
	{
		await _factory.CreateTestUserAsync("admin", "Admin123!@#", isAdmin: true);
		await _client.LoginAsync("admin", "Admin123!@#", _factory.JsonOptions);
	}

	[Fact]
	public async Task Create_WithExpiresAt_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
		var createDto = new CreateUserDto
		{
			UserName = "expiringuser",
			Password = "Expire123!@#",
			Email = "expire@test.com",
			ExpiresAt = expiresAt
		};

		// Act
		var response = await _client.PostAsJsonAsync("/system/api/admin/users", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("expiringuser", result.UserName);
		Assert.NotNull(result.ExpiresAt);
		// Compare with tolerance for serialization rounding
		Assert.True(Math.Abs((result.ExpiresAt.Value - expiresAt).TotalSeconds) < 2);
	}

	[Fact]
	public async Task Create_WithClaims_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var createDto = new CreateUserDto
		{
			UserName = "claimsuser",
			Password = "Claims123!@#",
			Email = "claims@test.com",
			Claims = new List<UserClaimDto>
			{
				new() { Type = "department", Value = "Engineering" },
				new() { Type = "employee_id", Value = "EMP-001" }
			}
		};

		// Act
		var response = await _client.PostAsJsonAsync("/system/api/admin/users", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal("claimsuser", result.UserName);
		Assert.Equal(2, result.Claims.Count);
		Assert.Contains(result.Claims, c => c.Type == "department" && c.Value == "Engineering");
		Assert.Contains(result.Claims, c => c.Type == "employee_id" && c.Value == "EMP-001");
	}

	[Fact]
	public async Task Update_ExpiresAt_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var user = await _factory.CreateTestUserAsync("updateexpire");
		var shortGuid = new ShortGuid(user.Id);
		var newExpiresAt = DateTimeOffset.UtcNow.AddDays(90);

		var updateDto = new { ExpiresAt = newExpiresAt };

		// Act
		var response = await _client.PatchAsJsonAsync($"/system/api/admin/users/{shortGuid}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.NotNull(result.ExpiresAt);
		Assert.True(Math.Abs((result.ExpiresAt.Value - newExpiresAt).TotalSeconds) < 2);
	}

	[Fact]
	public async Task Update_Claims_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var user = await _factory.CreateTestUserAsync("updateclaims");
		var shortGuid = new ShortGuid(user.Id);

		var updateDto = new
		{
			Claims = new[]
			{
				new { Type = "location", Value = "New York" },
				new { Type = "team", Value = "Backend" }
			}
		};

		// Act
		var response = await _client.PatchAsJsonAsync($"/system/api/admin/users/{shortGuid}", updateDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Equal(2, result.Claims.Count);
		Assert.Contains(result.Claims, c => c.Type == "location" && c.Value == "New York");
		Assert.Contains(result.Claims, c => c.Type == "team" && c.Value == "Backend");
	}

	[Fact]
	public async Task Create_WithRoles_Succeeds()
	{
		// Arrange
		await LoginAsAdminAsync();
		var role = await _factory.CreateTestRoleAsync("UserTestRole");
		var roleShortGuid = new ShortGuid(role.Id);

		var createDto = new CreateUserDto
		{
			UserName = "userwithRoles",
			Password = "Roles123!@#",
			Email = "roles@test.com",
			Roles = [roleShortGuid]
		};

		// Act
		var response = await _client.PostAsJsonAsync("/system/api/admin/users", createDto, _factory.JsonOptions);

		// Assert
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var result = await response.ReadFromJsonAsync<UserDto>(_factory.JsonOptions);
		Assert.NotNull(result);
		Assert.Single(result.Roles);
		Assert.Equal(roleShortGuid.Value, result.Roles[0].Value);
	}
}
