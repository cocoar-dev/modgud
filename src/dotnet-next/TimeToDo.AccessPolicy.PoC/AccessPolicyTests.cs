using doob.Scripter.Core;
using doob.Scripter.Engine.Javascript;
using doob.Scripter.Engine.TypeScript;
using doob.Scripter.Shared;
using Jint;
using Microsoft.Extensions.DependencyInjection;

namespace TimeToDo.AccessPolicy.PoC;

public class AccessPolicyTests
{
    // ── Test data ──────────────────────────────────────────────────

    private static readonly Guid User1Id = Guid.NewGuid();
    private static readonly Guid User2Id = Guid.NewGuid();
    private static readonly Guid CustomerAId = Guid.NewGuid();
    private static readonly Guid CustomerBId = Guid.NewGuid();
    private static readonly Guid CustomerCId = Guid.NewGuid();

    private static List<SimpleTodoView> CreateTestTodos() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 1 - Customer A, User1 responsible",
            Customer = new SimpleViewRef { Id = CustomerAId, Label = "Kunde A" },
            Responsibles = [new SimpleViewRef { Id = User1Id, Label = "User 1" }],
            Status = "inProgress",
            CreatedBy = new SimpleViewRef { Id = User1Id, Label = "User 1" }
        },
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 2 - Customer B, User2 responsible",
            Customer = new SimpleViewRef { Id = CustomerBId, Label = "Kunde B" },
            Responsibles = [new SimpleViewRef { Id = User2Id, Label = "User 2" }],
            Status = "new",
            CreatedBy = new SimpleViewRef { Id = User2Id, Label = "User 2" }
        },
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 3 - Customer A, User2 responsible",
            Customer = new SimpleViewRef { Id = CustomerAId, Label = "Kunde A" },
            Responsibles = [new SimpleViewRef { Id = User2Id, Label = "User 2" }],
            Status = "done",
            CreatedBy = new SimpleViewRef { Id = User1Id, Label = "User 1" }
        },
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 4 - Customer C, no responsible",
            Customer = new SimpleViewRef { Id = CustomerCId, Label = "Kunde C" },
            Responsibles = [],
            Status = "new",
            CreatedBy = new SimpleViewRef { Id = User1Id, Label = "User 1" }
        },
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 5 - No customer, User1 responsible",
            Customer = null,
            Responsibles = [new SimpleViewRef { Id = User1Id, Label = "User 1" }],
            Status = "inProgress",
            CreatedBy = new SimpleViewRef { Id = User1Id, Label = "User 1" }
        },
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Todo 6 - Customer B, archived",
            Customer = new SimpleViewRef { Id = CustomerBId, Label = "Kunde B" },
            Responsibles = [new SimpleViewRef { Id = User1Id, Label = "User 1" }],
            Status = "done",
            IsArchived = true,
            CreatedBy = new SimpleViewRef { Id = User1Id, Label = "User 1" }
        }
    ];

    // ═══════════════════════════════════════════════════════════════
    // Test 1: Pure C# — QueryBuilder works without Jint
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void QueryBuilder_WithoutJint_FiltersCorrectly()
    {
        var todos = CreateTestTodos();

        // Simulate: User1 manages Customer A, and sees own todos
        var query = new TodoQueryBuilder()
            .WhereCustomerIn([CustomerAId])
            .WhereResponsible(User1Id);

        var result = query.Apply(todos);

        // Should see: Todo 1 (Customer A), Todo 3 (Customer A), Todo 5 (responsible), Todo 6 (responsible)
        Assert.Equal(4, result.Count);
        Assert.Contains(result, t => t.Title.Contains("Todo 1"));
        Assert.Contains(result, t => t.Title.Contains("Todo 3"));
        Assert.Contains(result, t => t.Title.Contains("Todo 5"));
        Assert.Contains(result, t => t.Title.Contains("Todo 6"));
    }

    [Fact]
    public void QueryBuilder_All_ReturnsEverything()
    {
        var todos = CreateTestTodos();

        var result = new TodoQueryBuilder()
            .All()
            .Apply(todos);

        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void QueryBuilder_NoFilters_DeniesAll()
    {
        var todos = CreateTestTodos();

        var result = new TodoQueryBuilder()
            .Apply(todos);

        Assert.Empty(result);
    }

    [Fact]
    public void QueryBuilder_ExcludeArchived_Works()
    {
        var todos = CreateTestTodos();

        var result = new TodoQueryBuilder()
            .WhereResponsible(User1Id)
            .ExcludeArchived()
            .Apply(todos);

        // User1 is responsible on Todo 1, 5, 6 — but 6 is archived
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, t => t.IsArchived);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 2: Pure Jint — JavaScript calls QueryBuilder methods
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Jint_CanCallQueryBuilder_WithJavaScript()
    {
        var todos = CreateTestTodos();
        var ctx = new AccessContext
        {
            UserId = User1Id,
            ManagedCustomerIds = [CustomerAId]
        };
        var query = new TodoQueryBuilder();

        // JavaScript policy script
        const string script = """
            // ctx and query are CLR objects passed from C#
            query.WhereCustomerIn(ctx.ManagedCustomerIds.ToArray());
            query.WhereResponsible(ctx.UserId);
        """;

        var engine = new Jint.Engine(options =>
            options.AllowClr(
                typeof(AccessContext).Assembly,
                typeof(Guid).Assembly));

        engine.SetValue("ctx", ctx);
        engine.SetValue("query", query);
        engine.Execute(script);

        var result = query.Apply(todos);

        // Same as pure C# test: Todo 1, 3, 5, 6
        Assert.Equal(4, result.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 3: Jint with conditional logic in script
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Jint_ConditionalPolicy_AdminSeesAll()
    {
        var todos = CreateTestTodos();
        var adminCtx = new AccessContext
        {
            UserId = User1Id,
            Permissions = ["app:admin"],
            ManagedCustomerIds = [CustomerAId]  // irrelevant for admin
        };
        var query = new TodoQueryBuilder();

        const string script = """
            if (ctx.HasPermission("todo:read-all")) {
                query.All();
            } else {
                if (ctx.ManagedCustomerIds.Count > 0) {
                    query.WhereCustomerIn(ctx.ManagedCustomerIds.ToArray());
                }
                query.WhereResponsible(ctx.UserId);
            }
        """;

        var engine = new Jint.Engine(options =>
            options.AllowClr(
                typeof(AccessContext).Assembly,
                typeof(Guid).Assembly));

        engine.SetValue("ctx", adminCtx);
        engine.SetValue("query", query);
        engine.Execute(script);

        // Admin has app:admin → HasPermission everything = true → All()
        var result = query.Apply(todos);
        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void Jint_ConditionalPolicy_RegularUserFiltered()
    {
        var todos = CreateTestTodos();
        var userCtx = new AccessContext
        {
            UserId = User1Id,
            Permissions = ["todo:read", "todo:update"],
            ManagedCustomerIds = [CustomerAId]
        };
        var query = new TodoQueryBuilder();

        const string script = """
            if (ctx.HasPermission("todo:read-all")) {
                query.All();
            } else {
                if (ctx.ManagedCustomerIds.Count > 0) {
                    query.WhereCustomerIn(ctx.ManagedCustomerIds.ToArray());
                }
                query.WhereResponsible(ctx.UserId);
            }
        """;

        var engine = new Jint.Engine(options =>
            options.AllowClr(
                typeof(AccessContext).Assembly,
                typeof(Guid).Assembly));

        engine.SetValue("ctx", userCtx);
        engine.SetValue("query", query);
        engine.Execute(script);

        // No admin, no view-all → filtered by Customer A + responsible
        var result = query.Apply(todos);
        Assert.Equal(4, result.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 4: Full pipeline — Scripter with TypeScript
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scripter_TypeScript_FullPipeline()
    {
        var todos = CreateTestTodos();
        var ctx = new AccessContext
        {
            UserId = User1Id,
            Permissions = ["todo:read", "customer:read"],
            ManagedCustomerIds = [CustomerAId]
        };
        var query = new TodoQueryBuilder();

        // TypeScript policy — has type annotations!
        const string typeScript = """
            if (ctx.HasPermission("todo:read-all")) {
                query.All();
            } else {
                if (ctx.ManagedCustomerIds.Count > 0) {
                    query.WhereCustomerIn(ctx.ManagedCustomerIds.ToArray());
                }
                query.WhereResponsible(ctx.UserId);
                query.ExcludeArchived();
            }
        """;

        // Set up Scripter with TypeScript engine
        var services = new ServiceCollection();
        services.AddScripter(options => options
            .AddJavaScriptEngine()
            .AddTypeScriptEngine());
        var serviceProvider = services.BuildServiceProvider();

        var scripter = serviceProvider.GetRequiredService<IScripter>();

        // Step 1: Transpile TypeScript → JavaScript
        var tsEngine = scripter.GetScriptEngine("TypeScript");
        var javaScript = tsEngine.CompileScript(typeScript);

        // Step 2: Execute the transpiled JavaScript
        var jsEngine = scripter.GetScriptEngine("JavaScript");
        jsEngine.SetValue("ctx", ctx);
        jsEngine.SetValue("query", query);
        await jsEngine.ExecuteAsync(javaScript);

        // Step 3: Apply the built query
        var result = query.Apply(todos);

        // User1 manages Customer A + is responsible, archived excluded
        // Todo 1 (Customer A) ✓, Todo 3 (Customer A) ✓, Todo 5 (responsible) ✓
        // Todo 6 (responsible but archived) ✗
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, t => t.IsArchived);
        Assert.Contains(result, t => t.Title.Contains("Todo 1"));
        Assert.Contains(result, t => t.Title.Contains("Todo 3"));
        Assert.Contains(result, t => t.Title.Contains("Todo 5"));
    }

    // ═══════════════════════════════════════════════════════════════
    // Test 5: Performance — how fast is Jint evaluation?
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Jint_Performance_Benchmark()
    {
        // Generate a larger dataset
        var random = new Random(42);
        var customerIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var userIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var myUserId = userIds[0];
        var myCustomerIds = customerIds.Take(5).ToList();

        var todos = Enumerable.Range(0, 1000).Select(i => new SimpleTodoView
        {
            Id = Guid.NewGuid(),
            Title = $"Todo {i}",
            Customer = new SimpleViewRef { Id = customerIds[random.Next(customerIds.Count)], Label = $"Customer {i}" },
            Responsibles = [new SimpleViewRef { Id = userIds[random.Next(userIds.Count)], Label = $"User {i}" }],
            Status = i % 4 == 0 ? "done" : "inProgress",
            IsArchived = i % 10 == 0,
            CreatedBy = new SimpleViewRef { Id = userIds[random.Next(userIds.Count)], Label = $"Creator {i}" }
        }).ToList();

        var ctx = new AccessContext
        {
            UserId = myUserId,
            ManagedCustomerIds = myCustomerIds
        };

        const string script = """
            if (ctx.ManagedCustomerIds.Count > 0) {
                query.WhereCustomerIn(ctx.ManagedCustomerIds.ToArray());
            }
            query.WhereResponsible(ctx.UserId);
            query.ExcludeArchived();
        """;

        // Warm up Jint
        var engine = new Jint.Engine(options =>
            options.AllowClr(typeof(AccessContext).Assembly, typeof(Guid).Assembly));

        var query = new TodoQueryBuilder();
        engine.SetValue("ctx", ctx);
        engine.SetValue("query", query);
        engine.Execute(script);

        // Measure: Jint script execution (builds the query)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 100;
        for (var i = 0; i < iterations; i++)
        {
            var q = new TodoQueryBuilder();
            var eng = new Jint.Engine(options =>
                options.AllowClr(typeof(AccessContext).Assembly, typeof(Guid).Assembly));
            eng.SetValue("ctx", ctx);
            eng.SetValue("query", q);
            eng.Execute(script);
        }
        sw.Stop();
        var avgJintMs = sw.Elapsed.TotalMilliseconds / iterations;

        // Measure: In-memory filter (simulates what happens if DB can't do it)
        sw.Restart();
        for (var i = 0; i < iterations; i++)
        {
            var q2 = new TodoQueryBuilder();
            q2.WhereCustomerIn(myCustomerIds.Cast<object>().ToArray());
            q2.WhereResponsible(myUserId);
            q2.ExcludeArchived();
            q2.Apply(todos);
        }
        sw.Stop();
        var avgFilterMs = sw.Elapsed.TotalMilliseconds / iterations;

        // Verify the filter produces results
        var finalResult = query.Apply(todos);
        Assert.NotEmpty(finalResult);

        // Write results to file (xunit.v3 swallows Console.WriteLine)
        var report = $"""
            === ABAC PoC Performance (1000 todos, {iterations} iterations) ===
            Jint script execution:  {avgJintMs:F2}ms avg
            In-memory filter:       {avgFilterMs:F2}ms avg
            Combined (Jint+filter): {avgJintMs + avgFilterMs:F2}ms avg
            Filtered: {finalResult.Count} of {todos.Count} todos visible
            """;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "benchmark-results.txt"), report);

        // Sanity check: it should be fast enough
        Assert.True(avgJintMs < 50, $"Jint execution too slow: {avgJintMs:F2}ms");
    }
}
