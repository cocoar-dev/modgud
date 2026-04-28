using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Tests.Todos;

[Collection(IntegrationTestCollection.Name)]
public class TodoArchiveTests : IntegrationTestBase
{
    public TodoArchiveTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Archive_Todo_NotReturnedInGetAll()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "To Archive", createdById: user.Id);
        var todoId = new ShortGuid(todo.Id).ToString();

        // Archive the todo
        var archiveResponse = await Client.PutAsJsonAsync(
            "/api/todo/archive",
            new List<string> { todoId },
            JsonOptions, TestContext.Current.CancellationToken);
        archiveResponse.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var results = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.DoesNotContain(results, t => t.Id == todoId);
    }

    [Fact]
    public async Task Archive_Todo_ReturnedInGetArchived()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Archived Todo", createdById: user.Id);
        var todoId = new ShortGuid(todo.Id).ToString();

        // Archive the todo
        var archiveResponse = await Client.PutAsJsonAsync(
            "/api/todo/archive",
            new List<string> { todoId },
            JsonOptions, TestContext.Current.CancellationToken);
        archiveResponse.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/todo/archive", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var results = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.Contains(results, t => t.Id == todoId);
        Assert.True(results.Single(t => t.Id == todoId).IsArchived);
    }

    [Fact]
    public async Task Restore_Todo_ReturnedInGetAll()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "To Restore", createdById: user.Id);
        var todoId = new ShortGuid(todo.Id).ToString();

        // Archive then restore
        await Client.PutAsJsonAsync("/api/todo/archive", new List<string> { todoId }, JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        var restoreResponse = await Client.PutAsJsonAsync(
            "/api/todo/archive?restore=true",
            new List<string> { todoId },
            JsonOptions, TestContext.Current.CancellationToken);
        restoreResponse.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var results = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.Contains(results, t => t.Id == todoId);
        Assert.False(results.Single(t => t.Id == todoId).IsArchived);
    }

    [Fact]
    public async Task Restore_Todo_NotReturnedInGetArchived()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "To Restore", createdById: user.Id);
        var todoId = new ShortGuid(todo.Id).ToString();

        // Archive then restore
        await Client.PutAsJsonAsync("/api/todo/archive", new List<string> { todoId }, JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        await Client.PutAsJsonAsync("/api/todo/archive?restore=true", new List<string> { todoId }, JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/todo/archive", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var results = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.DoesNotContain(results, t => t.Id == todoId);
    }

    [Fact]
    public async Task Archive_Multiple_Todos_ArchivesAll()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo1 = await Factory.CreateTestTodoAsync(title: "Todo 1", createdById: user.Id);
        var todo2 = await Factory.CreateTestTodoAsync(title: "Todo 2", createdById: user.Id);

        var ids = new List<string>
        {
            new ShortGuid(todo1.Id).ToString(),
            new ShortGuid(todo2.Id).ToString()
        };

        // Act
        var response = await Client.PutAsJsonAsync("/api/todo/archive", ids, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify both are archived
        var archiveResponse = await Client.GetAsync("/api/todo/archive", TestContext.Current.CancellationToken);
        var archived = await archiveResponse.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.Equal(2, archived.Count);
        Assert.All(archived, t => Assert.True(t.IsArchived));
    }

    [Fact]
    public async Task Archive_PreservesOtherFields()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var customer = await Factory.CreateTestCustomerAsync("Test Customer");

        var todo = await Factory.CreateTestTodoAsync(
            title: "Important Todo",
            description: "Important Description",
            status: TodoStatus.InProgress,
            customerId: customer.Id,
            createdById: user.Id,
            isCritical: true);

        var todoId = new ShortGuid(todo.Id).ToString();

        // Act
        await Client.PutAsJsonAsync("/api/todo/archive", new List<string> { todoId }, JsonOptions, TestContext.Current.CancellationToken);

        await Factory.WaitForProjectionsAsync();

        // Assert
        var response = await Client.GetAsync("/api/todo/archive", TestContext.Current.CancellationToken);
        var results = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        var result = results.Single(t => t.Id == todoId);

        Assert.Equal("Important Todo", result.Title);
        Assert.Equal("Important Description", result.Description);
        Assert.Equal(TodoStatus.InProgress, result.Status);
        Assert.NotNull(result.Customer);
        Assert.True(result.Critical);
    }
}
