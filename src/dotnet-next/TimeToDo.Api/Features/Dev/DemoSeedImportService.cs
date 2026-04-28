using System.Text.Json;
using System.Text.RegularExpressions;
using TimeToDo.Authorization.Access;
using TimeToDo.Authorization.Membership;
using TimeToDo.Authorization.Principals;
using Marten;
using Microsoft.AspNetCore.Identity;
using TimeToDo.Api.Features.Groups;
using TimeToDo.Domain.Comments.Events;
using TimeToDo.Domain.Customers.Events;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Events;
using TimeToDo.Domain.Todos.Events;
using TimeToDo.Domain.Users.Events;
using TimeToDo.Domain.ValueObjects;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;
using TimeToDo.Authentication;

namespace TimeToDo.Api.Features.Dev;

/// <summary>
/// Imports demo data from a JSON file. Creates events in the correct order,
/// resolves string-key references to GUIDs, and expands template variables in access scripts.
/// </summary>
public partial class DemoSeedImportService(IServiceProvider serviceProvider) : IDemoSeedService
{
    private readonly Dictionary<string, Guid> _userIds = new();
    private readonly Dictionary<string, Guid> _customerIds = new();
    private readonly Dictionary<string, Guid> _roleIds = new();
    private readonly Dictionary<string, Guid> _todoIds = new();
    // Groups keyed by their display name — used when a todo references a group as a responsible.
    private readonly Dictionary<string, Guid> _groupIdsByName = new(StringComparer.OrdinalIgnoreCase);

    public async Task<object> ImportAsync(string jsonPath)
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var data = JsonSerializer.Deserialize<DemoSeedData>(json, jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse demo seed JSON.");

        using var scope = serviceProvider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var membershipEvaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var autoMembershipRecalculator = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();

        var now = DateTime.UtcNow;
        var counts = new { Users = 0, Customers = 0, Roles = 0, Groups = 0, Todos = 0, Subtasks = 0, Comments = 0 };

        // ── Phase 1: Users ─────────────────────────────────────────
        foreach (var u in data.Users)
        {
            var id = Guid.CreateVersion7();
            _userIds[u.Key] = id;
            session.Events.StartStream<UserView>(id,
                new UserCreatedEvent(id, u.Firstname, u.Lastname, u.Acronym ?? u.Key, u.Email));
        }
        await session.SaveChangesAsync();
        await Task.Delay(500);

        // Identity setup (UserName + login capability)
        foreach (var u in data.Users)
        {
            var id = _userIds[u.Key];
            var userName = (u.Acronym ?? u.Key).ToLowerInvariant();
            session.Events.Append(id, new UserIdentitySetupEvent(id, userName, true));
        }
        await session.SaveChangesAsync();
        await Task.Delay(500);

        // ASP.NET Identity (ApplicationUser + password)
        Guid? adminUserId = null;
        foreach (var u in data.Users)
        {
            var id = _userIds[u.Key];
            var userName = (u.Acronym ?? u.Key).ToLowerInvariant();
            var appUser = new ApplicationUser(userName, u.Email)
            {
                Id = id,
                Firstname = u.Firstname,
                Lastname = u.Lastname,
                Acronym = u.Acronym ?? u.Key,
                IsActive = true
            };
            await userManager.CreateAsync(appUser, data.Password);

            if (u.IsAdmin)
                adminUserId = id;
        }

        // ── Phase 2: Customers ─────────────────────────────────────
        foreach (var c in data.Customers)
        {
            var id = Guid.CreateVersion7();
            _customerIds[c.Key] = id;
            session.Events.StartStream<CustomerView>(id,
                new CustomerCreatedEvent(id, c.Name, c.Important));
        }
        await session.SaveChangesAsync();
        await Task.Delay(500);

        // ── Phase 3: Roles ─────────────────────────────────────────
        foreach (var r in data.Roles)
        {
            var role = new PermissionRole
            {
                Id = Guid.CreateVersion7(),
                Name = r.Name,
                Description = r.Description,
                ResourceType = r.Resource,
                Permissions = r.Permissions
            };
            _roleIds[r.Key] = role.Id;
            session.Store(role);
            session.Events.StartStream(role.Id,
                new PermissionRoleCreatedEvent(role.Id, role.Name, role.Description, role.ResourceType, role.Permissions));
        }

        // Demo admin is joined to the existing "Administratoren" group created by Setup —
        // no duplicate group, no direct user→role grant. Group-membership is the sole path.
        if (adminUserId.HasValue)
        {
            var adminGroup = await session.Query<TimeToDo.Authorization.Principals.Group>()
                .Where(g => !g.IsDeleted && g.Name == "Administratoren")
                .FirstOrDefaultAsync();

            if (adminGroup is not null && !adminGroup.MemberIds.Contains(adminUserId.Value))
            {
                adminGroup.MemberIds.Add(adminUserId.Value);
                session.Store(adminGroup);
                session.Events.Append(adminGroup.Id, new GroupUpdatedEvent(
                    adminGroup.Id, adminGroup.Name, adminGroup.Description,
                    adminGroup.MemberIds, adminGroup.RoleIds, adminGroup.AccessScripts,
                    adminGroup.MembershipMode, adminGroup.MembershipScript, adminGroup.CompiledMembershipScript,
                    adminGroup.MembershipScriptDependencies,
                    adminGroup.Email, adminGroup.EmailMode));
            }
        }
        await session.SaveChangesAsync();

        // ── Phase 4: Groups ────────────────────────────────────────
        var autoGroups = new List<TimeToDo.Authorization.Principals.Group>();
        foreach (var g in data.Groups)
        {
            var memberIds = g.Members.Select(ResolveUserId).ToList();
            var roleIds = g.Roles.Select(ResolveRoleId).ToList();
            var scripts = g.Scripts.Select(s => new ResourceAccessScript
            {
                ResourceType = s.Resource,
                Script = ResolveTemplateVariables(s.Script),
                CompiledScript = ResolveTemplateVariables(s.Script)
            }).ToList();

            var mode = string.Equals(g.MembershipMode, "Auto", StringComparison.OrdinalIgnoreCase)
                ? MembershipMode.Auto
                : MembershipMode.Manual;

            string? membershipScript = null;
            string? compiledMembershipScript = null;
            List<string>? membershipDeps = null;
            if (mode == MembershipMode.Auto && !string.IsNullOrWhiteSpace(g.MembershipScript))
            {
                membershipScript = g.MembershipScript;
                compiledMembershipScript = membershipEvaluator.TranspileMembershipScript(g.MembershipScript);
                try { membershipDeps = membershipEvaluator.CollectDependencies<Principal>(compiledMembershipScript)?.ToList(); }
                catch { membershipDeps = null; }
            }

            var group = new TimeToDo.Authorization.Principals.Group
            {
                Id = Guid.CreateVersion7(),
                Name = g.Name,
                Description = g.Description,
                MemberIds = memberIds,
                RoleIds = roleIds,
                AccessScripts = scripts,
                MembershipMode = mode,
                MembershipScript = membershipScript,
                CompiledMembershipScript = compiledMembershipScript,
                MembershipScriptDependencies = membershipDeps,
                Email = g.Email,
                EmailMode = string.Equals(g.EmailMode, "ExpandToMembers", StringComparison.OrdinalIgnoreCase)
                    ? TimeToDo.Authorization.Principals.EmailMode.ExpandToMembers
                    : TimeToDo.Authorization.Principals.EmailMode.Shared,
            };
            session.Store(group);
            _groupIdsByName[group.Name] = group.Id;
            session.Events.StartStream(group.Id,
                new GroupCreatedEvent(group.Id, group.Name, group.Description,
                    group.MemberIds, group.RoleIds, group.AccessScripts,
                    group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                    group.MembershipScriptDependencies,
                    group.Email, group.EmailMode));

            if (mode == MembershipMode.Auto)
                autoGroups.Add(group);
        }
        await session.SaveChangesAsync();

        // Initial auto-membership recalc — runs one SQL per auto-group against PrincipalDirectory
        // (inline projection, already populated from Phase 1). Appends a
        // GroupMembershipRecomputedEvent per group that differs from initial MemberIds.
        foreach (var group in autoGroups)
        {
            await autoMembershipRecalculator.RecalculateForGroupAsync(group, session);
        }
        if (autoGroups.Count > 0)
            await session.SaveChangesAsync();

        // ── Phase 5: Todos ─────────────────────────────────────────
        foreach (var t in data.Todos)
        {
            var id = Guid.CreateVersion7();
            _todoIds[t.Key] = id;
            // Responsibles may be user keys OR group references ("@GroupName")
            var responsibleIds = t.Responsibles.Select(ResolvePrincipalId).ToList();
            // CreatedBy must be a human (events require a principal that can author) —
            // prefer the first user in the list; fall back to first responsible overall.
            var createdById = t.Responsibles
                .Where(r => !r.StartsWith('@'))
                .Select(ResolveUserId)
                .DefaultIfEmpty(responsibleIds.First())
                .First();

            session.Events.StartStream<TodoView>(id,
                new TodoCreatedEvent(id, t.Title, t.Description, ResolveDueDate(t.DueDate, now),
                    Enum.Parse<TodoStatus>(t.Status, ignoreCase: true),
                    t.Customer != null ? _customerIds[t.Customer] : null,
                    responsibleIds, null, t.Critical, t.AwaitingFeedback,
                    now, createdById));
        }
        await session.SaveChangesAsync();
        await Task.Delay(500);

        // ── Phase 6: Subtasks ──────────────────────────────────────
        var subtaskCount = 0;
        foreach (var st in data.Subtasks)
        {
            var parentId = _todoIds[st.Parent];
            var childId = Guid.CreateVersion7();
            var responsibleIds = st.Responsibles.Select(ResolveUserId).ToList();
            var createdById = responsibleIds.First();

            // Look up parent's customer
            var parentTodo = data.Todos.First(t => t.Key == st.Parent);
            var customerId = parentTodo.Customer != null ? _customerIds[parentTodo.Customer] : (Guid?)null;

            session.Events.StartStream<TodoView>(childId,
                new TodoCreatedEvent(childId, st.Title, null, ResolveDueDate(st.DueDate, now),
                    Enum.Parse<TodoStatus>(st.Status, ignoreCase: true),
                    customerId, responsibleIds, parentId,
                    st.Critical, false, now, createdById));

            session.Events.Append(parentId, new TodoChildAddedEvent(parentId, childId));
            subtaskCount++;
        }
        await session.SaveChangesAsync();
        await Task.Delay(500);

        // ── Phase 7: Comments ──────────────────────────────────────
        var commentCount = 0;
        var todoCommentCounts = new Dictionary<Guid, int>();
        foreach (var c in data.Comments)
        {
            var todoId = _todoIds[c.Todo];
            var commentId = Guid.CreateVersion7();
            var authorId = ResolveUserId(c.Author);

            session.Events.StartStream<CommentView>(commentId,
                new CommentCreatedEvent(commentId, c.Text, todoId, "Todo", now, authorId));

            // Track comment count per todo
            todoCommentCounts.TryAdd(todoId, 0);
            todoCommentCounts[todoId]++;

            // Read status
            foreach (var readerKey in c.ReadBy)
            {
                var readerId = ResolveUserId(readerKey);
                session.Events.Append(commentId,
                    new CommentMarkedAsReadEvent(commentId, readerId, now));
            }

            commentCount++;
        }

        // Update comment counts on todos
        foreach (var (todoId, count) in todoCommentCounts)
        {
            session.Events.Append(todoId, new TodoCommentsCountChangedEvent(todoId, count));
        }

        await session.SaveChangesAsync();
        await Task.Delay(500);

        return new
        {
            Message = "Demo environment seeded",
            Users = data.Users.Count,
            Customers = data.Customers.Count,
            Roles = data.Roles.Count,
            Groups = data.Groups.Count,
            Todos = data.Todos.Count,
            Subtasks = subtaskCount,
            Comments = commentCount,
            Password = data.Password
        };
    }

    private Guid ResolveUserId(string key) =>
        _userIds.TryGetValue(key, out var id) ? id
            : throw new InvalidOperationException($"Unknown user key: '{key}'");

    /// <summary>
    /// Resolves a principal reference in demo data. Keys prefixed with "@" point to
    /// groups by display name (e.g. "@Team West"); otherwise treated as user keys.
    /// </summary>
    private Guid ResolvePrincipalId(string key)
    {
        if (key.StartsWith('@'))
        {
            var groupName = key[1..];
            return _groupIdsByName.TryGetValue(groupName, out var gid) ? gid
                : throw new InvalidOperationException($"Unknown group name: '{groupName}'");
        }
        return ResolveUserId(key);
    }

    private Guid ResolveRoleId(string key) =>
        _roleIds.TryGetValue(key, out var id) ? id
            : throw new InvalidOperationException($"Unknown role key: '{key}'");

    private string ResolveTemplateVariables(string script)
    {
        return TemplatePattern().Replace(script, match =>
        {
            var path = match.Groups[1].Value; // e.g. "customers.acme" or "users.PM"
            var parts = path.Split('.', 2);
            if (parts.Length != 2) return match.Value;

            var (category, key) = (parts[0], parts[1]);
            var dict = category switch
            {
                "customers" => _customerIds,
                "users" => _userIds,
                _ => null
            };

            return dict != null && dict.TryGetValue(key, out var id)
                ? id.ToString()
                : throw new InvalidOperationException($"Cannot resolve template variable: '{path}'");
        });
    }

    private static DateTime? ResolveDueDate(string? spec, DateTime now)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        if (spec.StartsWith('+') || spec.StartsWith('-'))
        {
            var days = int.Parse(spec.TrimEnd('d'));
            return now.AddDays(days).Date;
        }
        return DateTime.Parse(spec);
    }

    [GeneratedRegex(@"\{\{(\w+\.\w+)\}\}")]
    private static partial Regex TemplatePattern();
}

// ── JSON DTOs ──────────────────────────────────────────────────────

file record DemoSeedData
{
    public string Password { get; init; } = "Demo1234!";
    public List<DemoUser> Users { get; init; } = [];
    public List<DemoCustomer> Customers { get; init; } = [];
    public List<DemoRole> Roles { get; init; } = [];
    public List<DemoGroup> Groups { get; init; } = [];
    public List<DemoTodo> Todos { get; init; } = [];
    public List<DemoSubtask> Subtasks { get; init; } = [];
    public List<DemoComment> Comments { get; init; } = [];
}

file record DemoUser
{
    public string Key { get; init; } = "";
    public string Firstname { get; init; } = "";
    public string Lastname { get; init; } = "";
    public string? Acronym { get; init; }
    public string Email { get; init; } = "";
    public bool IsAdmin { get; init; }
}

file record DemoCustomer
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Important { get; init; }
}

file record DemoRole
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Resource { get; init; } = "";
    public List<string> Permissions { get; init; } = [];
}

file record DemoGroup
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<string> Members { get; init; } = [];
    public List<string> Roles { get; init; } = [];
    public List<DemoScript> Scripts { get; init; } = [];
    public string? MembershipMode { get; init; }       // "Manual" (default) | "Auto"
    public string? MembershipScript { get; init; }     // TypeScript arrow-function on PrincipalDirectory
    public string? Email { get; init; }                // Optional notification email
    public string? EmailMode { get; init; }            // "Shared" (default) | "ExpandToMembers"
}

file record DemoScript
{
    public string Resource { get; init; } = "";
    public string Script { get; init; } = "";
}

file record DemoTodo
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string Status { get; init; } = "New";
    public string? Customer { get; init; }
    public List<string> Responsibles { get; init; } = [];
    public bool Critical { get; init; }
    public bool AwaitingFeedback { get; init; }
    public string? DueDate { get; init; }
}

file record DemoSubtask
{
    public string Parent { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "New";
    public List<string> Responsibles { get; init; } = [];
    public bool Critical { get; init; }
    public string? DueDate { get; init; }
}

file record DemoComment
{
    public string Todo { get; init; } = "";
    public string Text { get; init; } = "";
    public string Author { get; init; } = "";
    public List<string> ReadBy { get; init; } = [];
}
