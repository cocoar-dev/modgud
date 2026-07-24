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

    /// <summary>
    /// A user created with IsActive=false must READ BACK as inactive. The read
    /// model takes IsActive only from UserActivatedEvent / UserDeactivatedEvent
    /// and UserView defaults it to true, so setting the flag on the
    /// ApplicationUser document alone left the list query (and the admin grid)
    /// reporting the user as active while the document said otherwise. This
    /// asserts the projection, not just the create response.
    /// </summary>
    [Fact]
    public async Task Create_InactiveUser_ReadsBackAsInactive()
    {
        var createDto = new UserCreateDto
        {
            Firstname = "Staged",
            Lastname = "Starter",
            Email = "staged.starter@test.com",
            IsActive = false,
        };

        var response = await Client.PostAsJsonAsync("/api/user", createDto, JsonOptions, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.ReadSuccessJsonAsync<UserDto>(JsonOptions);
        Assert.False(created.IsActive);

        var readBack = await Client.GetAsync($"/api/user/{created.Id}", TestContext.Current.CancellationToken);
        readBack.EnsureSuccessStatusCode();
        var fetched = await readBack.ReadSuccessJsonAsync<UserDto>(JsonOptions);
        Assert.False(fetched.IsActive);
    }

    /// <summary>
    /// The create endpoint has always accepted an initial password; the admin
    /// form now offers it, so a user can be created ready to sign in instead of
    /// needing a second "set password" round-trip.
    /// </summary>
    [Fact]
    public async Task Create_UserWithPassword_ReportsHasPassword()
    {
        var createDto = new UserCreateDto
        {
            Firstname = "With",
            Lastname = "Password",
            Email = "with.password@test.com",
            Password = "ABC12abc!",
        };

        var response = await Client.PostAsJsonAsync("/api/user", createDto, JsonOptions, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.ReadSuccessJsonAsync<UserDto>(JsonOptions);

        Assert.True(created.HasPassword);
        Assert.True(created.IsActive);
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
    public async Task Delete_SingleUser_MovesToRecycleBin()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync("To Delete", "User");
        var userId = new ShortGuid(user.Id).ToString();

        // Act — admin "delete" is now a recycle-bin move (reversible), not an
        // immediate erase. The user stays queryable, flagged pending.
        var response = await Client.DeleteAsync($"/api/user/{userId}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        var getResponse = await Client.GetAsync($"/api/user/{userId}", TestContext.Current.CancellationToken);
        var dto = await getResponse.ReadSuccessJsonAsync<UserDto>(JsonOptions);
        Assert.True(dto.IsDeletionPending);
        Assert.Equal("Admin", dto.DeletionInitiator);
        Assert.NotNull(dto.DeletionDeadline);
    }

    [Fact]
    public async Task Delete_MultipleUsers_MovesAllToRecycleBin()
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

        // All three users remain queryable; the two deleted are now pending in
        // the recycle bin, DefaultUser is untouched.
        var getResponse = await Client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        var remaining = await getResponse.ReadSuccessJsonAsync<List<UserDto>>(JsonOptions);
        Assert.Equal(3, remaining.Count);

        var deletedIds = ids.ToHashSet();
        Assert.All(
            remaining.Where(u => deletedIds.Contains(u.Id)),
            u => Assert.True(u.IsDeletionPending));

        var defaultDto = remaining.Single(u => u.Id == new ShortGuid(DefaultUser!.Id).ToString());
        Assert.False(defaultDto.IsDeletionPending);
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
