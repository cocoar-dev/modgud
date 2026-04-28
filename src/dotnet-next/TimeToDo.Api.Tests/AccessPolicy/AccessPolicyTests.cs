using System.Net;
using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Application.DTOs.Todo;
using TimeToDo.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Authentication.Domain;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.AccessPolicy;

namespace TimeToDo.Api.Tests.AccessPolicy;

[Collection(IntegrationTestCollection.Name)]
public class AccessPolicyTests : IntegrationTestBase
{
    public AccessPolicyTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Admin_SeesAllTodosAndCustomers()
    {
        var user1 = await Factory.CreateTestUserAsync("Alice", "A", "AA");
        var user2 = await Factory.CreateTestUserAsync("Bob", "B", "BB");
        var customer = await Factory.CreateTestCustomerAsync("Acme Corp");

        await Factory.CreateTestTodoAsync(title: "Todo 1", responsibleUserIds: [user1.Id], customerId: customer.Id);
        await Factory.CreateTestTodoAsync(title: "Todo 2", responsibleUserIds: [user2.Id]);
        await Factory.CreateTestTodoAsync(title: "Todo 3");

        var todosResponse = await Client.GetAsync("/api/todo", TestContext.Current.CancellationToken);
        var customersResponse = await Client.GetAsync("/api/customer", TestContext.Current.CancellationToken);

        todosResponse.EnsureSuccessStatusCode();
        customersResponse.EnsureSuccessStatusCode();
        var todos = await todosResponse.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        var customers = await customersResponse.ReadSuccessJsonAsync<List<CustomerListDto>>(JsonOptions);

        Assert.Equal(3, todos.Count);
        Assert.Single(customers);
    }

    [Fact]
    public async Task WhereResponsible_UserSeesOnlyOwnTodos()
    {
        var userB = await Factory.CreateTestUserWithIdentityAsync(
            "Bob", "Test", "BT", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "User", "OU");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("BobGroup", [userB.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.Responsibles.some(r => r.Id === user.Id)")]);

        await Factory.CreateTestTodoAsync(title: "Bob's Todo", responsibleUserIds: [userB.Id]);
        await Factory.CreateTestTodoAsync(title: "Other's Todo", responsibleUserIds: [otherUser.Id]);
        await Factory.CreateTestTodoAsync(title: "Nobody's Todo");

        using var bobClient = await CreateAuthenticatedClientAsync("bt", "TestPass1234");
        var response = await bobClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Single(todos);
        Assert.Equal("Bob's Todo", todos[0].Title);
    }

    [Fact]
    public async Task All_UserSeesAllTodos()
    {
        var userC = await Factory.CreateTestUserWithIdentityAsync(
            "Charlie", "Test", "CT", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("CharlieGroup", [userC.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript("todo", "(t) => true")]);

        await Factory.CreateTestTodoAsync(title: "Todo 1");
        await Factory.CreateTestTodoAsync(title: "Todo 2");
        await Factory.CreateTestTodoAsync(title: "Todo 3");

        using var charlieClient = await CreateAuthenticatedClientAsync("ct", "TestPass1234");
        var response = await charlieClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(3, todos.Count);
    }

    [Fact]
    public async Task DefaultDeny_UserWithNoScripts_SeesNothing()
    {
        // User has todo:read permission (passes endpoint gate) but no access scripts
        // → scope filter is empty → zero rows visible.
        var userD = await Factory.CreateTestUserWithIdentityAsync(
            "Dave", "Test", "DT", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("TodoRead", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("EmptyGroup", [userD.Id], roleIds: [role.Id]);

        await Factory.CreateTestTodoAsync(title: "Todo 1");
        await Factory.CreateTestTodoAsync(title: "Todo 2");

        using var daveClient = await CreateAuthenticatedClientAsync("dt", "TestPass1234");
        var response = await daveClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Empty(todos);
    }

    [Fact]
    public async Task WhereCreatedBy_UserSeesOnlyCreatedTodos()
    {
        var userE = await Factory.CreateTestUserWithIdentityAsync(
            "Eve", "Test", "ET", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "User", "OU");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("EveGroup", [userE.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.CreatedBy != null && t.CreatedBy.Id === user.Id")]);

        await Factory.CreateTestTodoAsync(title: "Eve's Todo", createdById: userE.Id);
        await Factory.CreateTestTodoAsync(title: "Other's Todo", createdById: otherUser.Id);

        using var eveClient = await CreateAuthenticatedClientAsync("et", "TestPass1234");
        var response = await eveClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Single(todos);
        Assert.Equal("Eve's Todo", todos[0].Title);
    }

    [Fact]
    public async Task MultipleGroups_CombineWithOrSemantics()
    {
        var userF = await Factory.CreateTestUserWithIdentityAsync(
            "Frank", "Test", "FT", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "User", "OU");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);

        // Two groups with different scripts — should OR-combine
        await Factory.CreateTestGroupAsync("FrankResponsible", [userF.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.Responsibles.some(r => r.Id === user.Id)")]);
        await Factory.CreateTestGroupAsync("FrankCreated", [userF.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.CreatedBy != null && t.CreatedBy.Id === user.Id")]);

        await Factory.CreateTestTodoAsync(title: "Frank responsible", responsibleUserIds: [userF.Id]);
        await Factory.CreateTestTodoAsync(title: "Frank created", createdById: userF.Id);
        await Factory.CreateTestTodoAsync(title: "Unrelated", createdById: otherUser.Id, responsibleUserIds: [otherUser.Id]);

        using var frankClient = await CreateAuthenticatedClientAsync("ft", "TestPass1234");
        var response = await frankClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(2, todos.Count);
        Assert.Contains(todos, t => t.Title == "Frank responsible");
        Assert.Contains(todos, t => t.Title == "Frank created");
    }

    [Fact]
    public async Task Predicate_IsCritical_FiltersNonCriticalTodos()
    {
        var userG = await Factory.CreateTestUserWithIdentityAsync(
            "Grace", "Test", "GT", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("GraceGroup", [userG.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript("todo", "(t) => t.IsCritical")]);

        await Factory.CreateTestTodoAsync(title: "Critical Todo", isCritical: true);
        await Factory.CreateTestTodoAsync(title: "Normal Todo", isCritical: false);
        await Factory.CreateTestTodoAsync(title: "Also Critical", isCritical: true);

        using var graceClient = await CreateAuthenticatedClientAsync("gt", "TestPass1234");
        var response = await graceClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(2, todos.Count);
        Assert.All(todos, t => Assert.True(t.Critical));
    }

    [Fact]
    public async Task CustomerFilter_NameStartsWith_FiltersCustomers()
    {
        var userH = await Factory.CreateTestUserWithIdentityAsync(
            "Helen", "Test", "HT", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("CustomerAccess", "customer", ["read"]);
        await Factory.CreateTestGroupAsync("HelenGroup", [userH.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "customer", "(c) => c.Name != null && c.Name.startsWith('A')")]);

        await Factory.CreateTestCustomerAsync("Acme Corp");
        await Factory.CreateTestCustomerAsync("Alpha Inc");
        await Factory.CreateTestCustomerAsync("Beta Ltd");

        using var helenClient = await CreateAuthenticatedClientAsync("ht", "TestPass1234");
        var response = await helenClient.GetAsync("/api/customer", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var customers = await response.ReadSuccessJsonAsync<List<CustomerListDto>>(JsonOptions);
        Assert.Equal(2, customers.Count);
        Assert.All(customers, c => Assert.StartsWith("A", c.Name));
    }

    [Fact]
    public async Task WriteProtection_PutInaccessibleTodo_Returns403()
    {
        var userI = await Factory.CreateTestUserWithIdentityAsync(
            "Ivan", "Test", "IT", password: "TestPass1234", permissions: []);
        var otherUser = await Factory.CreateTestUserAsync("Other", "User", "OU");

        var role = await Factory.CreateTestRoleAsync("TodoFull", "todo", ["read", "update"]);
        await Factory.CreateTestGroupAsync("IvanGroup", [userI.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.Responsibles.some(r => r.Id === user.Id)")]);

        var ownTodo = await Factory.CreateTestTodoAsync(title: "Ivan's Todo", responsibleUserIds: [userI.Id]);
        var otherTodo = await Factory.CreateTestTodoAsync(title: "Other's Todo", responsibleUserIds: [otherUser.Id]);

        using var ivanClient = await CreateAuthenticatedClientAsync("it", "TestPass1234");

        var updateDto = new TodoUpdateDto { Title = new Optional<string>("Hacked") };
        var forbiddenResponse = await ivanClient.PutAsJsonAsync(
            $"/api/todo/{new ShortGuid(otherTodo.Id)}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        var allowedResponse = await ivanClient.PutAsJsonAsync(
            $"/api/todo/{new ShortGuid(ownTodo.Id)}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        allowedResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Predicate_CustomerIdIncludes_FiltersByCustomerIds()
    {
        var userJ = await Factory.CreateTestUserWithIdentityAsync(
            "Julia", "Test", "JT", password: "TestPass1234", permissions: []);

        var customer1 = await Factory.CreateTestCustomerAsync("Acme Corp");
        var customer2 = await Factory.CreateTestCustomerAsync("Beta Ltd");
        var customer3 = await Factory.CreateTestCustomerAsync("Gamma Inc");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        // linq.guid(...) produces a typed Guid constant at translation time — array.includes → Enumerable.Contains
        var script = $"(t) => t.Customer != null && (t.Customer.Id === linq.guid('{customer1.Id}') || t.Customer.Id === linq.guid('{customer2.Id}'))";
        await Factory.CreateTestGroupAsync("JuliaGroup", [userJ.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript("todo", script)]);

        await Factory.CreateTestTodoAsync(title: "Acme Todo", customerId: customer1.Id);
        await Factory.CreateTestTodoAsync(title: "Beta Todo", customerId: customer2.Id);
        await Factory.CreateTestTodoAsync(title: "Gamma Todo", customerId: customer3.Id);
        await Factory.CreateTestTodoAsync(title: "No Customer Todo");

        using var juliaClient = await CreateAuthenticatedClientAsync("jt", "TestPass1234");
        var response = await juliaClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(2, todos.Count);
        Assert.Contains(todos, t => t.Title == "Acme Todo");
        Assert.Contains(todos, t => t.Title == "Beta Todo");
    }

    [Fact]
    public async Task Predicate_StatusEquals_FiltersByStatus()
    {
        var userK = await Factory.CreateTestUserWithIdentityAsync(
            "Karl", "Test", "KT", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        // Enum coercion: string literal on enum property → typed enum constant
        await Factory.CreateTestGroupAsync("KarlGroup", [userK.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.Status === 'InProgress'")]);

        await Factory.CreateTestTodoAsync(title: "InProgress Todo", status: TodoStatus.InProgress);
        await Factory.CreateTestTodoAsync(title: "New Todo", status: TodoStatus.New);
        await Factory.CreateTestTodoAsync(title: "Done Todo", status: TodoStatus.Done);

        using var karlClient = await CreateAuthenticatedClientAsync("kt", "TestPass1234");
        var response = await karlClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Single(todos);
        Assert.Equal("InProgress Todo", todos[0].Title);
    }

    [Fact]
    public async Task Predicate_NestedPropertyStartsWith_FiltersByCustomerName()
    {
        var userL = await Factory.CreateTestUserWithIdentityAsync(
            "Lisa", "Test", "LT", password: "TestPass1234", permissions: []);

        var customer1 = await Factory.CreateTestCustomerAsync("Acme Corp");
        var customer2 = await Factory.CreateTestCustomerAsync("Alpha Inc");
        var customer3 = await Factory.CreateTestCustomerAsync("Beta Ltd");

        var role = await Factory.CreateTestRoleAsync("TodoAccess", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("LisaGroup", [userL.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", "(t) => t.Customer != null && t.Customer.Label != null && t.Customer.Label.startsWith('A')")]);

        await Factory.CreateTestTodoAsync(title: "Acme Todo", customerId: customer1.Id);
        await Factory.CreateTestTodoAsync(title: "Alpha Todo", customerId: customer2.Id);
        await Factory.CreateTestTodoAsync(title: "Beta Todo", customerId: customer3.Id);

        using var lisaClient = await CreateAuthenticatedClientAsync("lt", "TestPass1234");
        var response = await lisaClient.GetAsync("/api/todo", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var todos = await response.ReadSuccessJsonAsync<List<TodoDto>>(JsonOptions);
        Assert.Equal(2, todos.Count);
        Assert.Contains(todos, t => t.Title == "Acme Todo");
        Assert.Contains(todos, t => t.Title == "Alpha Todo");
    }

    [Fact]
    public async Task Coupled_PermissionAndScopeAreNotMixedAcrossGroups()
    {
        // Ivan is in two groups:
        //   ReadGroup   → role "todo:read"   + scope "CustomerId === X"
        //   UpdateGroup → role "todo:update" + scope "CustomerId === Y"
        // Coupled semantics: Read action uses only ReadGroup's scope, Update action
        // uses only UpdateGroup's scope. The old Union would grant both for each action.
        var ivan = await Factory.CreateTestUserWithIdentityAsync(
            "Ivan", "Coupled", "IC", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("X-Customer");
        var customerY = await Factory.CreateTestCustomerAsync("Y-Customer");

        var readRole   = await Factory.CreateTestRoleAsync("CoupledRead",   "todo", ["read"]);
        var updateRole = await Factory.CreateTestRoleAsync("CoupledUpdate", "todo", ["update"]);

        await Factory.CreateTestGroupAsync("ReadGroup", [ivan.Id],
            roleIds: [readRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);
        await Factory.CreateTestGroupAsync("UpdateGroup", [ivan.Id],
            roleIds: [updateRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerY.Id}')")]);

        var todoX = await Factory.CreateTestTodoAsync(title: "Todo X", customerId: customerX.Id);
        var todoY = await Factory.CreateTestTodoAsync(title: "Todo Y", customerId: customerY.Id);

        using var scope = Factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<TimeToDo.Infrastructure.AccessPolicy.IAccessPolicyEngine>();

        // Read uses only the read-group's scope
        Assert.True(await engine.CanAccessTodoForActionAsync(ivan.Id, todoX.Id, "todo:read"));
        Assert.False(await engine.CanAccessTodoForActionAsync(ivan.Id, todoY.Id, "todo:read"));

        // Update uses only the update-group's scope
        Assert.False(await engine.CanAccessTodoForActionAsync(ivan.Id, todoX.Id, "todo:update"));
        Assert.True(await engine.CanAccessTodoForActionAsync(ivan.Id, todoY.Id, "todo:update"));

        // Diagnostic union-variant still grants both (unchanged behavior)
        Assert.True(await engine.CanAccessTodoAsync(ivan.Id, todoX.Id));
        Assert.True(await engine.CanAccessTodoAsync(ivan.Id, todoY.Id));
    }

    [Fact]
    public async Task Coupled_UpdateEndpoint_RejectsWithWriteScopeMismatch()
    {
        // Same split-permission setup as above, but exercised through the HTTP endpoint
        // to verify the handler uses the coupled variant: PUT against X (read-scoped, not
        // update-scoped) must return 403 even though the user has todo:update.
        var ivan = await Factory.CreateTestUserWithIdentityAsync(
            "Ivan", "Split", "IS", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("X-Customer");
        var customerY = await Factory.CreateTestCustomerAsync("Y-Customer");

        var readRole   = await Factory.CreateTestRoleAsync("SplitRead",   "todo", ["read"]);
        var updateRole = await Factory.CreateTestRoleAsync("SplitUpdate", "todo", ["update"]);

        await Factory.CreateTestGroupAsync("ReadGroup2", [ivan.Id],
            roleIds: [readRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);
        await Factory.CreateTestGroupAsync("UpdateGroup2", [ivan.Id],
            roleIds: [updateRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerY.Id}')")]);

        var todoX = await Factory.CreateTestTodoAsync(title: "Todo X", customerId: customerX.Id);
        var todoY = await Factory.CreateTestTodoAsync(title: "Todo Y", customerId: customerY.Id);

        using var ivanClient = await CreateAuthenticatedClientAsync("is", "TestPass1234");
        var updateDto = new TodoUpdateDto { Title = new Optional<string>("Hacked") };

        var updateX = await ivanClient.PutAsJsonAsync($"/api/todo/{new ShortGuid(todoX.Id)}", updateDto, JsonOptions, TestContext.Current.CancellationToken);
        var updateY = await ivanClient.PutAsJsonAsync($"/api/todo/{new ShortGuid(todoY.Id)}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, updateX.StatusCode);
        updateY.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Scope_GetTodoById_ReturnsNotFoundForOutOfScopeRow()
    {
        // Existence-leak prevention: a todo outside the user's read scope must look like
        // "not found" rather than 403 Forbidden. Prevents probing IDs to enumerate rows.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Nora", "Scope", "NS", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("Scope-X");
        var customerY = await Factory.CreateTestCustomerAsync("Scope-Y");

        var role = await Factory.CreateTestRoleAsync("ScopedRead", "todo", ["read"]);
        await Factory.CreateTestGroupAsync("ScopedGroup", [user.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);

        var todoInScope = await Factory.CreateTestTodoAsync(title: "In scope", customerId: customerX.Id);
        var todoOutOfScope = await Factory.CreateTestTodoAsync(title: "Out of scope", customerId: customerY.Id);

        using var client = await CreateAuthenticatedClientAsync("ns", "TestPass1234");

        var insideResponse = await client.GetAsync($"/api/todo/{new ShortGuid(todoInScope.Id)}", TestContext.Current.CancellationToken);
        var outsideResponse = await client.GetAsync($"/api/todo/{new ShortGuid(todoOutOfScope.Id)}", TestContext.Current.CancellationToken);

        insideResponse.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, outsideResponse.StatusCode);
    }

    [Fact]
    public async Task Proto_CreateTodo_ForbidsOutOfScopeCustomer()
    {
        // User has todo:create + todo:read scoped to customerX only. The proto-eval step
        // must reject a POST /api/todo whose CustomerId references customerY.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Proto", "User", "PU", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("Proto-X");
        var customerY = await Factory.CreateTestCustomerAsync("Proto-Y");

        var role = await Factory.CreateTestRoleAsync("ProtoTodoCreate", "todo", ["read", "create"]);
        await Factory.CreateTestGroupAsync("ProtoGroup", [user.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);

        using var client = await CreateAuthenticatedClientAsync("pu", "TestPass1234");

        var allowedDto = new TodoCreateDto
        {
            Title = "Allowed",
            Status = TodoStatus.New,
            Customer = new RefPropertyDto { Id = new ShortGuid(customerX.Id).ToString() },
        };
        var forbiddenDto = new TodoCreateDto
        {
            Title = "Forbidden",
            Status = TodoStatus.New,
            Customer = new RefPropertyDto { Id = new ShortGuid(customerY.Id).ToString() },
        };

        var allowed = await client.PostAsJsonAsync("/api/todo", allowedDto, JsonOptions, TestContext.Current.CancellationToken);
        var forbidden = await client.PostAsJsonAsync("/api/todo", forbiddenDto, JsonOptions, TestContext.Current.CancellationToken);

        allowed.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Verifies that Marten's STJ serializer can deserialize old camelCase enum values
    /// from existing event store data (backward compatibility after PascalCase migration).
    /// </summary>
    [Fact]
    public void MartenSerializer_DeserializesCamelCaseEnums_BackwardCompatible()
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Marten.IDocumentStore>();
        var serializer = store.Options.Serializer();

        var camelCaseJson = """{"Status":"inProgress","Title":"Test"}"""u8;
        var pascalCaseJson = """{"Status":"InProgress","Title":"Test"}"""u8;

        var fromCamel = serializer.FromJson<TestStatusHolder>(new System.IO.MemoryStream(camelCaseJson.ToArray()));
        var fromPascal = serializer.FromJson<TestStatusHolder>(new System.IO.MemoryStream(pascalCaseJson.ToArray()));

        Assert.Equal(TodoStatus.InProgress, fromCamel.Status);
        Assert.Equal(TodoStatus.InProgress, fromPascal.Status);
    }

    private record TestStatusHolder(TodoStatus Status, string Title);
}
