using BuildingBlocks.Helper;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.User;
using Modgud.Domain.Common;

namespace Modgud.Api.Tests.Users;

[Collection(IntegrationTestCollection.Name)]
public class UserCrudTests : IntegrationTestBase
{
    public UserCrudTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_User_ReturnsCreatedUser()
    {
        // Arrange
        var createDto = new UserCreateDto
        {
            Firstname = "John",
            Lastname = "Doe",
            Acronym = "JD",
            Email = "john.doe@test.com"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/user", createDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<UserDto>(JsonOptions);

        Assert.NotNull(result.Id);
        Assert.Equal("John", result.Firstname);
        Assert.Equal("Doe", result.Lastname);
        Assert.Equal("JD", result.Acronym);
        Assert.Equal("john.doe@test.com", result.Email);
    }

    [Fact]
    public async Task Get_AllUsers_ReturnsAllUsers()
    {
        // Arrange (note: DefaultUser is already created by IntegrationTestBase)
        await Factory.CreateTestUserAsync("Alice", "Smith", "AS");
        await Factory.CreateTestUserAsync("Bob", "Jones", "BJ");

        // Act
        var response = await Client.GetAsync("/api/user", TestContext.Current.CancellationToken);

        // Assert
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"HTTP {(int)response.StatusCode}: {body}");
        }
        var result = await response.ReadSuccessJsonAsync<List<UserDto>>(JsonOptions);

        // 3 users: DefaultUser (TU) + Alice + Bob
        Assert.Equal(3, result.Count);
        Assert.Contains(result, u => u.Firstname == "Alice");
        Assert.Contains(result, u => u.Firstname == "Bob");
    }

    [Fact]
    public async Task Get_UserById_ReturnsUser()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync("John", "Doe", "JD");

        // Act
        var response = await Client.GetAsync($"/api/user/{new ShortGuid(user.Id)}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<UserDto>(JsonOptions);

        Assert.Equal(new ShortGuid(user.Id).ToString(), result.Id);
        Assert.Equal("John", result.Firstname);
        Assert.Equal("Doe", result.Lastname);
    }

    [Fact]
    public async Task Get_NonExistentUser_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync($"/api/user/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_User_ReturnsUpdatedUser()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync("John", "Doe", "JD");
        var userId = new ShortGuid(user.Id).ToString();

        var updateDto = new UserUpdateDto
        {
            Firstname = new Optional<string>("Jane"),
            Lastname = new Optional<string>("Smith"),
            Acronym = new Optional<string>("JS"),
            Email = new Optional<string>("jane.smith@test.com")
        };

        // Act
        var response = await Client.PutAsJsonAsync($"/api/user/{userId}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<UserDto>(JsonOptions);

        Assert.Equal("Jane", result.Firstname);
        Assert.Equal("Smith", result.Lastname);
        Assert.Equal("JS", result.Acronym);
        Assert.Equal("jane.smith@test.com", result.Email);
    }

    [Fact]
    public async Task Delete_SingleUser_RemovesFromDatabase()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync("To Delete", "User");
        var userId = new ShortGuid(user.Id).ToString();

        // Act
        var response = await Client.DeleteAsync($"/api/user/{userId}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify it's gone
        var getResponse = await Client.GetAsync($"/api/user/{userId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_MultipleUsers_RemovesAllFromDatabase()
    {
        // Arrange (note: DefaultUser is already created by IntegrationTestBase)
        var user1 = await Factory.CreateTestUserAsync("User", "One");
        var user2 = await Factory.CreateTestUserAsync("User", "Two");

        var ids = new List<string>
        {
            new ShortGuid(user1.Id).ToString(),
            new ShortGuid(user2.Id).ToString()
        };

        // Act
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/user")
        {
            Content = JsonContent.Create(ids, options: JsonOptions)
        };
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify user1 and user2 are gone (DefaultUser still remains)
        var getResponse = await Client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        var remaining = await getResponse.ReadSuccessJsonAsync<List<UserDto>>(JsonOptions);
        Assert.Single(remaining); // Only DefaultUser remains
        Assert.Equal("TU", remaining[0].Acronym);
    }

    [Fact]
    public async Task Get_Users_WithPagination_ReturnsPagedResults()
    {
        // Arrange
        for (int i = 1; i <= 5; i++)
        {
            await Factory.CreateTestUserAsync($"User{i}", "Test", $"U{i}");
        }

        // Act
        var response = await Client.GetAsync("/api/user?skip=2&take=2", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<UserDto>>(JsonOptions);

        Assert.Equal(2, result.Count);
    }
}
