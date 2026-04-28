using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;

namespace TimeToDo.Api.Tests.Todos;

[Collection(IntegrationTestCollection.Name)]
public class TodoParentChildTests : IntegrationTestBase
{
    public TodoParentChildTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_ChildTodo_InheritsParentCustomer()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var customer = await Factory.CreateTestCustomerAsync("Parent Customer");
        var parentTodo = await Factory.CreateTestTodoAsync(
            title: "Parent Todo",
            customerId: customer.Id,
            createdById: user.Id);

        var createDto = new TodoCreateDto
        {
            Title = "Child Todo",
            Status = TodoStatus.New
        };

        // Act - Create child using query parameter
        var response = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(parentTodo.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);
        // Assert
        response.EnsureSuccessStatusCode();
        var createResult = await response.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        // Verify via re-fetch (async projection resolves parent reference and customer)
        await Factory.WaitForProjectionsAsync();
        var result = await Client.GetFromJsonAsync<TodoDto>($"/api/todo/{createResult.Id}", JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(new ShortGuid(parentTodo.Id).ToString(), result!.ParentTodoId);
        Assert.NotNull(result.Customer);
        Assert.Equal(new ShortGuid(customer.Id).ToString(), result.Customer.Id);
        Assert.Equal("Parent Customer", result.Customer.Label);
    }

    [Fact]
    public async Task Move_TodoToParent_InheritsParentCustomer()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentCustomer = await Factory.CreateTestCustomerAsync("Parent Customer");
        var childCustomer = await Factory.CreateTestCustomerAsync("Child Customer");

        var parentTodo = await Factory.CreateTestTodoAsync(
            title: "Parent Todo",
            customerId: parentCustomer.Id,
            createdById: user.Id);

        var childTodo = await Factory.CreateTestTodoAsync(
            title: "Child Todo",
            customerId: childCustomer.Id,
            createdById: user.Id);

        // Act
        var response = await Client.PostAsync(
            $"/api/todo/{new ShortGuid(childTodo.Id)}/move-into/{new ShortGuid(parentTodo.Id)}",
            null);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify the child now has parent's customer
        var getResponse = await Client.GetAsync($"/api/todo/{new ShortGuid(childTodo.Id)}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.Equal(new ShortGuid(parentTodo.Id).ToString(), result.ParentTodoId);
        Assert.NotNull(result.Customer);
        Assert.Equal(new ShortGuid(parentCustomer.Id).ToString(), result.Customer.Id);
    }

    [Fact]
    public async Task Move_TodoToParent_UpdatesChildTodoIds()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentTodo = await Factory.CreateTestTodoAsync(title: "Parent Todo", createdById: user.Id);
        var childTodo = await Factory.CreateTestTodoAsync(title: "Child Todo", createdById: user.Id);

        // Act
        var response = await Client.PostAsync(
            $"/api/todo/{new ShortGuid(childTodo.Id)}/move-into/{new ShortGuid(parentTodo.Id)}",
            null);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify parent has child in ChildTodoIds
        var parentDoc = await Factory.GetDocumentAsync<TodoView>(parentTodo.Id);
        Assert.NotNull(parentDoc);
        Assert.Contains(childTodo.Id, parentDoc.ChildTodoIds);
    }

    [Fact]
    public async Task Convert_ChildToParent_ClearsParentTodoId()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentTodo = await Factory.CreateTestTodoAsync(title: "Parent Todo", createdById: user.Id);

        // Create child via query parameter
        var createDto = new TodoCreateDto { Title = "Child Todo", Status = TodoStatus.New };
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(parentTodo.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);
        var childTodo = await createResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        await Factory.WaitForProjectionsAsync();

        // Act - Convert to parent
        var response = await Client.PostAsJsonAsync(
            "/api/todo/convert-to-parent",
            new List<string> { childTodo.Id },
            JsonOptions, TestContext.Current.CancellationToken);
        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Verify child no longer has parent
        var getResponse = await Client.GetAsync($"/api/todo/{childTodo.Id}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.Null(result.ParentTodoId);
    }

    [Fact]
    public async Task Cannot_CreateSubtodoOfSubtodo()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var grandparentTodo = await Factory.CreateTestTodoAsync(title: "Grandparent", createdById: user.Id);

        // Create first level child
        var createDto1 = new TodoCreateDto { Title = "Parent (child of grandparent)", Status = TodoStatus.New };
        var response1 = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(grandparentTodo.Id)}",
            createDto1,
            JsonOptions, TestContext.Current.CancellationToken);
        var parentTodo = await response1.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        // Wait for async projection so the parent's TodoView has ParentTodoId set
        await Factory.WaitForProjectionsAsync();

        // Act - Try to create grandchild
        var createDto2 = new TodoCreateDto { Title = "Grandchild", Status = TodoStatus.New };
        var response = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={parentTodo.Id}",
            createDto2,
            JsonOptions, TestContext.Current.CancellationToken);
        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cannot_MoveParentWithChildrenToBeChild()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentWithChildren = await Factory.CreateTestTodoAsync(title: "Parent with children", createdById: user.Id);
        var targetParent = await Factory.CreateTestTodoAsync(title: "Target Parent", createdById: user.Id);

        // Create a child for the first parent
        var createDto = new TodoCreateDto { Title = "Child", Status = TodoStatus.New };
        await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(parentWithChildren.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);
        // Wait for async projection so the parent's TodoView has ChildTodoIds populated
        await Factory.WaitForProjectionsAsync();

        // Act - Try to move parent (which has children) into target
        var response = await Client.PostAsync(
            $"/api/todo/{new ShortGuid(parentWithChildren.Id)}/move-into/{new ShortGuid(targetParent.Id)}",
            null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Parent_ChildrenBecomeRootTodos()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentTodo = await Factory.CreateTestTodoAsync(title: "Parent", createdById: user.Id);

        // Create child
        var createDto = new TodoCreateDto { Title = "Child", Status = TodoStatus.New };
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(parentTodo.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);
        var childTodo = await createResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        await Factory.WaitForProjectionsAsync();

        // Act - Delete parent
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/todo")
        {
            Content = JsonContent.Create(new List<string> { new ShortGuid(parentTodo.Id).ToString() }, options: JsonOptions)
        };
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Child should now be a root todo (no parent)
        var getResponse = await Client.GetAsync($"/api/todo/{childTodo.Id}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        Assert.Null(result.ParentTodoId);
    }

    [Fact]
    public async Task Delete_Child_RemovedFromParentChildTodoIds()
    {
        // Arrange
        var user = await Factory.CreateTestUserAsync();
        var parentTodo = await Factory.CreateTestTodoAsync(title: "Parent", createdById: user.Id);

        // Create child
        var createDto = new TodoCreateDto { Title = "Child", Status = TodoStatus.New };
        var createResponse = await Client.PostAsJsonAsync(
            $"/api/todo?parentTodo={new ShortGuid(parentTodo.Id)}",
            createDto,
            JsonOptions, TestContext.Current.CancellationToken);
        var childTodo = await createResponse.ReadSuccessJsonAsync<TodoDto>(JsonOptions);

        await Factory.WaitForProjectionsAsync();

        // Verify parent has child
        var parentDoc = await Factory.GetDocumentAsync<TodoView>(parentTodo.Id);
        Assert.Contains(ShortGuid.Decode(childTodo.Id), parentDoc!.ChildTodoIds);

        // Act - Delete child
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/todo")
        {
            Content = JsonContent.Create(new List<string> { childTodo.Id }, options: JsonOptions)
        };
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();

        await Factory.WaitForProjectionsAsync();

        // Parent should no longer have child in list
        parentDoc = await Factory.GetDocumentAsync<TodoView>(parentTodo.Id);
        Assert.DoesNotContain(ShortGuid.Decode(childTodo.Id), parentDoc!.ChildTodoIds);
    }
}
