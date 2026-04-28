using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.Common;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Api.Tests.Todos;

[Collection(IntegrationTestCollection.Name)]
public class TodoCrudTests : IntegrationTestBase
{
    public TodoCrudTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_Todo_ReturnsCreatedTodo()
    {
        // Arrange
        var createDto = new TodoCreateDto
        {
            Title = "Test Todo",
            Description = "Test Description",
            Status = TodoStatus.New
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/todo", createDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.NotNull(result.Id);
        Assert.Equal("Test Todo", result.Title);
        Assert.Equal("Test Description", result.Description);
        Assert.Equal(TodoStatus.New, result.Status);
        Assert.False(result.IsArchived);
        Assert.NotNull(result.CreatedAt);
    }

    [Fact]
    public async Task Create_Todo_WithCustomer_ReturnsCustomerLabel()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("Acme Corp");
        var createDto = new TodoCreateDto
        {
            Title = "Customer Todo",
            Status = TodoStatus.New,
            Customer = new RefPropertyDto { Id = new ShortGuid(customer.Id).ToString() }
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/todo", createDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var createResult = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        // Verify via re-fetch (async projection resolves labels)
        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{createResult.Id}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(result!.Customer);
        Assert.Equal(new ShortGuid(customer.Id).ToString(), result.Customer.Id);
        Assert.Equal("Acme Corp", result.Customer.Label);
    }

    [Fact]
    public async Task Get_AllTodos_ReturnsNonArchivedOnly()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var activeTodo = await Factory.CreateTestTodoAsync(title: "Active Todo", createdById: user.Id);
        var archivedTodo = await Factory.CreateTestTodoAsync(title: "Archived Todo", createdById: user.Id);

        // Archive one todo
        var archiveResponse = await Client.PutAsJsonAsync(
            "/api/todo/archive",
            new List<string> { new ShortGuid(archivedTodo.Id).ToString() },
            JsonOptions, TestContext.Current.CancellationToken);
        archiveResponse.EnsureSuccessStatusCode();

        // Wait for async projection to process the archive event
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);

        Assert.Single(result);
        Assert.Equal("Active Todo", result[0].Title);
    }

    [Fact]
    public async Task Get_TodoById_ReturnsTodo()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Test Todo",
            description: "Test Description",
            createdById: user.Id);

        // Act
        var response = await Client.GetAsync($"/api/todo/{new ShortGuid(todo.Id)}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.Equal(new ShortGuid(todo.Id).ToString(), result.Id);
        Assert.Equal("Test Todo", result.Title);
        Assert.Equal("Test Description", result.Description);
    }

    [Fact]
    public async Task Get_NonExistentTodo_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync($"/api/todo/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_Todo_ReturnsUpdatedTodo()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(
            title: "Original Title",
            description: "Original Description",
            createdById: user.Id);

        var updateDto = new TodoUpdateDto
        {
            Title = new Optional<string>("Updated Title"),
            Description = new Optional<string?>("Updated Description"),
            Status = new Optional<TodoStatus>(TodoStatus.InProgress)
        };

        // Act
        var response = await Client.PutAsJsonAsync($"/api/todo/{new ShortGuid(todo.Id)}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.Equal("Updated Title", result.Title);
        Assert.Equal("Updated Description", result.Description);
        Assert.Equal(TodoStatus.InProgress, result.Status);
    }

    [Fact]
    public async Task Delete_Todo_RemovesFromDatabase()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "To Delete", createdById: user.Id);
        var todoId = new ShortGuid(todo.Id).ToString();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/todo")
        {
            Content = JsonContent.Create(new List<string> { todoId }, options: JsonOptions)
        };
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        // Wait for async projection to process the delete event
        await Factory.WaitForProjectionsAsync();

        // Verify it's gone
        var getResponse = await Client.GetAsync($"/api/todo/{todoId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_Todo_WithResponsibles_ReturnsResponsiblesWithLabels()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync("John", "Doe", "JD");
        var createDto = new TodoCreateDto
        {
            Title = "Todo with Responsible",
            Status = TodoStatus.New,
            Responsibles = new List<RefPropertyDto>
            {
                new RefPropertyDto { Id = new ShortGuid(user.Id).ToString() }
            }
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/todo", createDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var createResult = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        // Verify via re-fetch (async projection resolves labels)
        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{createResult.Id}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.Single(result!.Responsibles);
        Assert.Equal(new ShortGuid(user.Id).ToString(), result.Responsibles[0].Id);
        Assert.Contains("JD", result.Responsibles[0].Label!);
    }
}
