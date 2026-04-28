using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.Comment;

namespace TimeToDo.Api.Tests.Comments;

[Collection(IntegrationTestCollection.Name)]
public class CommentCrudTests : IntegrationTestBase
{
    public CommentCrudTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_Comment_ReturnsCreatedComment()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        var createDto = new CommentCreateDto
        {
            Description = "This is a test comment"
        };

        // Act
        var response = await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);

        Assert.NotNull(result.Id);
        Assert.Equal("This is a test comment", result.Description);
        Assert.NotNull(result.CreatedAt);
    }

    [Fact]
    public async Task Get_AllComments_ReturnsAllComments()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "Comment 1" },
            JsonOptions, TestContext.Current.CancellationToken);

        await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "Comment 2" },
            JsonOptions, TestContext.Current.CancellationToken);

        // Wait for async projections to create CommentViews
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/comment", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CommentListDto>>(JsonOptions);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Get_CommentsByReferenceId_ReturnsOnlyRelatedComments()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo1 = await Factory.CreateTestTodoAsync(title: "Todo 1", createdById: user.Id);
        var todo2 = await Factory.CreateTestTodoAsync(title: "Todo 2", createdById: user.Id);

        await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo1.Id)}",
            new CommentCreateDto { Description = "Comment for Todo 1" },
            JsonOptions, TestContext.Current.CancellationToken);

        await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo2.Id)}",
            new CommentCreateDto { Description = "Comment for Todo 2" },
            JsonOptions, TestContext.Current.CancellationToken);

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync($"/api/comment/todo/{new ShortGuid(todo1.Id)}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CommentListDto>>(JsonOptions);

        Assert.Single(result);
        Assert.Equal("Comment for Todo 1", result[0].Description);
    }

    [Fact]
    public async Task Get_CommentById_ReturnsComment()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        var createResponse = await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "Test Comment" },
            JsonOptions, TestContext.Current.CancellationToken);
        var created = await createResponse.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync($"/api/comment/{created.Id}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);

        Assert.Equal(created.Id, result.Id);
        Assert.Equal("Test Comment", result.Description);
    }

    [Fact]
    public async Task Get_NonExistentComment_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync($"/api/comment/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Comment_RemovesFromDatabase()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        var createResponse = await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "To Delete" },
            JsonOptions, TestContext.Current.CancellationToken);
        var created = await createResponse.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);

        // Wait for async projection so the handler can load CommentView
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.DeleteAsync($"/api/comment/{created.Id}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify it's gone
        var getResponse = await Client.GetAsync($"/api/comment/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task MarkAsRead_SetsIHaveReadTrue()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        var createResponse = await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "Test Comment" },
            JsonOptions, TestContext.Current.CancellationToken);
        var created = await createResponse.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);

        // Wait for async projection so the handler can load CommentView
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.PostAsync($"/api/comment/{created.Id}/read", null, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify - Note: IHaveRead depends on the current user context
        // In the test environment, the read status is recorded
        var getResponse = await Client.GetAsync($"/api/comment/{created.Id}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<CommentListDto>(JsonOptions);
        // IHaveRead will be true if the user who reads is the same as who marked it read
        Assert.True(result.IHaveRead);
    }

    [Fact]
    public async Task Create_Comment_UpdatesTodoCommentsCount()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        // Get initial todo
        var initialResponse = await Client.GetAsync($"/api/todo/{new ShortGuid(todo.Id)}", TestContext.Current.CancellationToken);
        var initialTodo = await initialResponse.ReadSuccessJsonAsync<TimeToDo.Application.DTOs.Todo.TodoDto>(JsonOptions);
        var initialCount = initialTodo.CommentsCount;

        // Act - Create comment
        await Client.PostAsJsonAsync(
            $"/api/comment/todo/{new ShortGuid(todo.Id)}",
            new CommentCreateDto { Description = "New Comment" },
            JsonOptions, TestContext.Current.CancellationToken);

        // Wait for async projection to process the TodoCommentsCountChangedEvent
        await Factory.WaitForProjectionsAsync();

        // Assert
        var getResponse = await Client.GetAsync($"/api/todo/{new ShortGuid(todo.Id)}", TestContext.Current.CancellationToken);
        var updatedTodo = await getResponse.ReadSuccessJsonAsync<TimeToDo.Application.DTOs.Todo.TodoDto>(JsonOptions);

        Assert.Equal(initialCount + 1, updatedTodo.CommentsCount);
    }

    [Fact]
    public async Task Get_Comments_WithPagination_ReturnsPagedResults()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var todo = await Factory.CreateTestTodoAsync(title: "Test Todo", createdById: user.Id);

        for (int i = 1; i <= 5; i++)
        {
            await Client.PostAsJsonAsync(
                $"/api/comment/todo/{new ShortGuid(todo.Id)}",
                new CommentCreateDto { Description = $"Comment {i}" },
                JsonOptions, TestContext.Current.CancellationToken);
        }

        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/comment?skip=2&take=2", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CommentListDto>>(JsonOptions);

        Assert.Equal(2, result.Count);
    }
}
