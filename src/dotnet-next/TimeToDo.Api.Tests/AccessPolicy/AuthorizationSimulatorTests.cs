using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Infrastructure.AccessPolicy;

namespace TimeToDo.Api.Tests.AccessPolicy;

[Collection(IntegrationTestCollection.Name)]
public class AuthorizationSimulatorTests : IntegrationTestBase
{
    public AuthorizationSimulatorTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Simulate_UserWithoutPermission_ReportsPermissionDenied()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Sim", "NoPerm", "SN", password: "TestPass1234", permissions: []);

        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        var result = await simulator.SimulateAsync(user.Id, "todo", resourceId: null, action: "update", ct: TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOutcome.PermissionDenied, result.Outcome);
        Assert.False(result.PermissionGranted);
        Assert.Equal("todo:update", result.RequiredPermission);
        Assert.Empty(result.PermissionTrace);
    }

    [Fact]
    public async Task Simulate_AdminUser_BypassesEverything()
    {
        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        // DefaultUser is seeded with app:admin by IntegrationTestBase.
        var result = await simulator.SimulateAsync(DefaultUser!.Id, "todo", resourceId: null, action: "delete");

        Assert.Equal(SimulationOutcome.Allowed, result.Outcome);
        Assert.True(result.AdminBypass);
        Assert.True(result.PermissionGranted);
    }

    [Fact]
    public async Task Simulate_RowInScope_ReportsAllowedWithTrace()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Sim", "InScope", "SI", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("Sim-X");
        var role = await Factory.CreateTestRoleAsync("SimRole", "todo", ["read", "update"]);
        await Factory.CreateTestGroupAsync("SimGroupInScope", [user.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);

        var todo = await Factory.CreateTestTodoAsync(title: "In scope", customerId: customerX.Id);

        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        var result = await simulator.SimulateAsync(user.Id, "todo", todo.Id, "update", TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOutcome.Allowed, result.Outcome);
        Assert.True(result.PermissionGranted);
        Assert.True(result.RowInScope);
        Assert.Contains(result.PermissionTrace, t => t.GroupName == "SimGroupInScope" && t.Permission == "todo:update");
        Assert.Contains(result.ScopeTrace, t => t.GroupName == "SimGroupInScope");
    }

    [Fact]
    public async Task Simulate_RowOutOfScope_ReportsScopeDenied()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Sim", "OutScope", "SO", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("Sim-X2");
        var customerY = await Factory.CreateTestCustomerAsync("Sim-Y2");

        var role = await Factory.CreateTestRoleAsync("SimOutRole", "todo", ["read", "update"]);
        await Factory.CreateTestGroupAsync("SimGroupOutScope", [user.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);

        var todoOutside = await Factory.CreateTestTodoAsync(title: "Out of scope", customerId: customerY.Id);

        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        var result = await simulator.SimulateAsync(user.Id, "todo", todoOutside.Id, "update", TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOutcome.ScopeDenied, result.Outcome);
        Assert.True(result.PermissionGranted);
        Assert.False(result.RowInScope);
    }

    [Fact]
    public async Task Simulate_UnknownRow_ReportsResourceNotFound()
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Sim", "NoRow", "SNR", password: "TestPass1234", permissions: []);

        var role = await Factory.CreateTestRoleAsync("SimNoRowRole", "todo", ["read", "update"]);
        await Factory.CreateTestGroupAsync("SimNoRowGroup", [user.Id],
            roleIds: [role.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript("todo", "(t) => true")]);

        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        var result = await simulator.SimulateAsync(user.Id, "todo", Guid.NewGuid(), "update", TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOutcome.ResourceNotFound, result.Outcome);
    }

    [Fact]
    public async Task Simulate_SplitGroups_OnlyContributingGroupAppearsInScopeTrace()
    {
        // Coupled model: read-only group's scope must NOT leak into update simulation.
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Sim", "Split", "SS", password: "TestPass1234", permissions: []);

        var customerX = await Factory.CreateTestCustomerAsync("Sim-Split-X");
        var customerY = await Factory.CreateTestCustomerAsync("Sim-Split-Y");

        var readRole = await Factory.CreateTestRoleAsync("SimSplitRead", "todo", ["read"]);
        var updateRole = await Factory.CreateTestRoleAsync("SimSplitUpdate", "todo", ["update"]);

        await Factory.CreateTestGroupAsync("SimReadOnly", [user.Id],
            roleIds: [readRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerX.Id}')")]);
        await Factory.CreateTestGroupAsync("SimWriteOnly", [user.Id],
            roleIds: [updateRole.Id],
            accessScripts: [TimeTodoWebApplicationFactory.BuildAccessScript(
                "todo", $"(t) => t.Customer != null && t.Customer.Id === linq.guid('{customerY.Id}')")]);

        var todoY = await Factory.CreateTestTodoAsync(title: "Only write-group sees this", customerId: customerY.Id);

        using var scope = Factory.Services.CreateScope();
        var simulator = scope.ServiceProvider.GetRequiredService<IAuthorizationSimulator>();

        var result = await simulator.SimulateAsync(user.Id, "todo", todoY.Id, "update", TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOutcome.Allowed, result.Outcome);
        Assert.Contains(result.ScopeTrace, t => t.GroupName == "SimWriteOnly");
        Assert.DoesNotContain(result.ScopeTrace, t => t.GroupName == "SimReadOnly");
    }

}
