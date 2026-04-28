using System.Net;
using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Tests.Todos;

[Collection(IntegrationTestCollection.Name)]
public class TodoStatusFlagsTests : IntegrationTestBase
{
    public TodoStatusFlagsTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task UpdateStatus_Bulk_UpdatesAllTodos()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo1 = await Factory.CreateTestTodoAsync(title: "Todo 1", status: TodoStatus.New, createdById: user.Id);
        var todo2 = await Factory.CreateTestTodoAsync(title: "Todo 2", status: TodoStatus.New, createdById: user.Id);

        var statusUpdate = new TodoStatusUpdateRequestDto
        {
            Ids = new List<string>
            {
                new ShortGuid(todo1.Id).ToString(),
                new ShortGuid(todo2.Id).ToString()
            },
            Status = TodoStatus.InProgress
        };

        // Act
        var response = await Client.PutAsJsonAsync("/api/todo/update/status", statusUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify via re-fetch
        await Factory.WaitForProjectionsAsync();
        var result1 = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo1.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        var result2 = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo2.Id)}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(TodoStatus.InProgress, result1!.Status);
        Assert.Equal(TodoStatus.InProgress, result2!.Status);
    }

    [Fact]
    public async Task UpdateStatus_PreservesOtherFields()
    {
        // Arrange - This is a KEY regression test
        var user = await Factory.CreateTestUserAsync();
        var customer = await Factory.CreateTestCustomerAsync("Test Customer");

        var todo = await Factory.CreateTestTodoAsync(
            title: "Important Todo",
            description: "Important Description",
            status: TodoStatus.New,
            customerId: customer.Id,
            createdById: user.Id,
            isCritical: true,
            isAwaitingFeedback: true);

        var statusUpdate = new TodoStatusUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            Status = TodoStatus.Done
        };

        // Act
        var response = await Client.PutAsJsonAsync("/api/todo/update/status", statusUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify via re-fetch
        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);

        // Status should be updated
        Assert.Equal(TodoStatus.Done, result!.Status);

        // All other fields should be preserved
        Assert.Equal("Important Todo", result.Title);
        Assert.Equal("Important Description", result.Description);
        Assert.NotNull(result.Customer);
        Assert.Equal(new ShortGuid(customer.Id).ToString(), result.Customer.Id);
        Assert.True(result.Critical);
        Assert.True(result.AwaitingFeedback);
    }

    [Fact]
    public async Task PatchFlags_AddCritical_SetsCriticalTrue()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Test Todo",
            isCritical: false,
            createdById: user.Id);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            AddFlags = new List<string> { "critical" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        Assert.True(result!.Critical);
    }

    [Fact]
    public async Task PatchFlags_RemoveCritical_SetsCriticalFalse()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Critical Todo",
            isCritical: true,
            createdById: user.Id);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            RemoveFlags = new List<string> { "critical" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        Assert.False(result!.Critical);
    }

    [Fact]
    public async Task PatchFlags_AddAwaitingFeedback_SetsAwaitingFeedbackTrue()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Test Todo",
            isAwaitingFeedback: false,
            createdById: user.Id);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            AddFlags = new List<string> { "awaitingfeedback" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        Assert.True(result!.AwaitingFeedback);
    }

    [Fact]
    public async Task PatchFlags_RemoveAwaitingFeedback_SetsAwaitingFeedbackFalse()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Test Todo",
            isAwaitingFeedback: true,
            createdById: user.Id);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            RemoveFlags = new List<string> { "awaitingfeedback" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        Assert.False(result!.AwaitingFeedback);
    }

    [Fact]
    public async Task PatchFlags_Bulk_UpdatesAllTodos()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo1 = await Factory.CreateTestTodoAsync(title: "Todo 1", isCritical: false, createdById: user.Id);
        var todo2 = await Factory.CreateTestTodoAsync(title: "Todo 2", isCritical: false, createdById: user.Id);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string>
            {
                new ShortGuid(todo1.Id).ToString(),
                new ShortGuid(todo2.Id).ToString()
            },
            AddFlags = new List<string> { "critical" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result1 = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo1.Id)}", JsonOptions, TestContext.Current.CancellationToken);
        var result2 = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo2.Id)}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.True(result1!.Critical);
        Assert.True(result2!.Critical);
    }

    [Fact]
    public async Task PatchFlags_PreservesOtherFields()
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
            isCritical: false,
            isAwaitingFeedback: true);

        var flagsUpdate = new TodoFlagsUpdateRequestDto
        {
            Ids = new List<string> { new ShortGuid(todo.Id).ToString() },
            AddFlags = new List<string> { "critical" }
        };

        // Act
        var response = await Client.PatchAsJsonAsync("/api/todo/update/flags", flagsUpdate, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{new ShortGuid(todo.Id)}", JsonOptions, TestContext.Current.CancellationToken);

        // Flag should be updated
        Assert.True(result!.Critical);

        // All other fields should be preserved
        Assert.Equal("Important Todo", result.Title);
        Assert.Equal("Important Description", result.Description);
        Assert.Equal(TodoStatus.InProgress, result.Status);
        Assert.NotNull(result.Customer);
        Assert.True(result.AwaitingFeedback);
    }
}
