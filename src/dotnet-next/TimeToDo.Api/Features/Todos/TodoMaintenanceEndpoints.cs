using Marten;
using Microsoft.AspNetCore.Mvc;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;
using TimeToDo.Infrastructure.Persistence.Marten.Repositories;

namespace TimeToDo.Api.Features.Todos;

public static class TodoMaintenanceEndpoints
{
    public static WebApplication MapTodoMaintenanceEndpoints(this WebApplication app, string path)
    {
        var maintenanceGroup = app.MapGroup($"{path}/maintenance/todos")
            .WithTags("Todo Maintenance")
            .RequireAuthorization()
            .RequiresPermission("app:admin");
            

        // Reconcile all parent-child relationships
        maintenanceGroup.MapPost("reconcile-children", async (
            [FromServices] IDocumentSession session,
            [FromQuery] bool dryRun = true) =>
        {
            var results = new List<ReconciliationResult>();
            var todos = await session.Query<TodoDocument>().ToListAsync();

            foreach (var todo in todos)
            {
                // Query actual children from database (ParentTodoId is source of truth)
                var actualChildren = await session.Query<TodoDocument>()
                    .Where(t => t.ParentTodoId == todo.Id)
                    .Select(t => t.Id)
                    .ToListAsync();

                var actualChildrenSet = actualChildren.ToHashSet();
                var currentChildrenSet = todo.ChildTodoIds.ToHashSet();

                // Check if lists don't match
                if (!actualChildrenSet.SetEquals(currentChildrenSet))
                {
                    var result = new ReconciliationResult
                    {
                        TodoId = todo.Id.ToString(),
                        TodoTitle = todo.Title,
                        CurrentChildCount = todo.ChildTodoIds.Count,
                        ActualChildCount = actualChildren.Count,
                        MissingChildren = actualChildren.Except(todo.ChildTodoIds).ToList(),
                        ExtraChildren = todo.ChildTodoIds.Except(actualChildren).ToList()
                    };
                    results.Add(result);

                    // Fix if not dry run
                    if (!dryRun)
                    {
                        todo.ChildTodoIds = actualChildren.ToList();
                        session.Update(todo);
                    }
                }
            }

            if (!dryRun && results.Any())
            {
                await session.SaveChangesAsync();
            }

            return Results.Ok(new
            {
                DryRun = dryRun,
                TotalTodos = todos.Count,
                InconsistenciesFound = results.Count,
                Fixed = !dryRun,
                Details = results
            });
        })
        .WithName("ReconcileAllTodoChildren")
        .WithDescription("Reconcile all parent-child relationships (fixes ChildTodoIds based on ParentTodoId)");

        // Fix orphaned children in ChildTodoIds (set their ParentTodoId)
        maintenanceGroup.MapPost("fix-orphaned-children", async (
            [FromServices] IDocumentSession session,
            [FromQuery] bool dryRun = true) =>
        {
            var results = new List<OrphanFixResult>();
            var todos = await session.Query<TodoDocument>().ToListAsync();

            foreach (var parent in todos)
            {
                // Check each child in the parent's ChildTodoIds list
                foreach (var childId in parent.ChildTodoIds)
                {
                    var child = await session.LoadAsync<TodoDocument>(childId);
                    
                    if (child == null)
                    {
                        // Child doesn't exist - we'll report but not fix (should use reconcile-children to clean up)
                        results.Add(new OrphanFixResult
                        {
                            ParentId = parent.Id.ToString(),
                            ParentTitle = parent.Title,
                            ChildId = childId.ToString(),
                            ChildTitle = null,
                            PreviousParentId = null,
                            Action = "ChildNotFound",
                            Message = $"Child {childId} does not exist in database"
                        });
                    }
                    else if (child.ParentTodoId != parent.Id)
                    {
                        // Child exists but has wrong/null ParentTodoId - FIX IT!
                        var previousParentId = child.ParentTodoId?.ToString();
                        
                        results.Add(new OrphanFixResult
                        {
                            ParentId = parent.Id.ToString(),
                            ParentTitle = parent.Title,
                            ChildId = child.Id.ToString(),
                            ChildTitle = child.Title,
                            PreviousParentId = previousParentId,
                            Action = !dryRun ? "Fixed" : "WouldFix",
                            Message = previousParentId == null 
                                ? $"Set ParentTodoId from null to {parent.Id}"
                                : $"Changed ParentTodoId from {previousParentId} to {parent.Id}"
                        });

                        if (!dryRun)
                        {
                            // Remove from old parent's ChildTodoIds if it had a different parent
                            if (child.ParentTodoId.HasValue && child.ParentTodoId.Value != parent.Id)
                            {
                                var oldParent = await session.LoadAsync<TodoDocument>(child.ParentTodoId.Value);
                                if (oldParent != null && oldParent.ChildTodoIds.Contains(childId))
                                {
                                    oldParent.ChildTodoIds.Remove(childId);
                                    session.Update(oldParent);
                                }
                            }

                            // Set the correct parent
                            child.ParentTodoId = parent.Id;
                            // Inherit parent's customer
                            child.CustomerId = parent.CustomerId;
                            session.Update(child);
                        }
                    }
                }
            }

            if (!dryRun && results.Any())
            {
                await session.SaveChangesAsync();
            }

            return Results.Ok(new
            {
                DryRun = dryRun,
                TotalParentsChecked = todos.Count,
                IssuesFound = results.Count,
                Fixed = !dryRun,
                Details = results
            });
        })
        .WithName("FixOrphanedChildrenInParentLists")
        .WithDescription("Fix children that are in parent's ChildTodoIds but don't have correct ParentTodoId set");

        // Validate parent-child consistency
        maintenanceGroup.MapGet("validate-relationships", async (
            [FromServices] IDocumentSession session) =>
        {
            var issues = new List<ValidationIssue>();
            var todos = await session.Query<TodoDocument>().ToListAsync();

            foreach (var todo in todos)
            {
                // Check if parent exists when ParentTodoId is set
                if (todo.ParentTodoId.HasValue)
                {
                    var parent = await session.LoadAsync<TodoDocument>(todo.ParentTodoId.Value);
                    if (parent == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            TodoId = todo.Id.ToString(),
                            TodoTitle = todo.Title,
                            ParentId = todo.ParentTodoId.Value.ToString(),
                            ParentTitle = null,
                            IssueType = "OrphanedChild",
                            Description = $"ParentTodoId {todo.ParentTodoId.Value} does not exist"
                        });
                    }
                    else if (!parent.ChildTodoIds.Contains(todo.Id))
                    {
                        issues.Add(new ValidationIssue
                        {
                            TodoId = todo.Id.ToString(),
                            TodoTitle = todo.Title,
                            ParentId = parent.Id.ToString(),
                            ParentTitle = parent.Title,
                            IssueType = "MissingFromParent",
                            Description = $"Not in parent's ChildTodoIds list"
                        });
                    }
                }

                // Check if all children in ChildTodoIds actually have this todo as parent
                foreach (var childId in todo.ChildTodoIds)
                {
                    var child = await session.LoadAsync<TodoDocument>(childId);
                    if (child == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            TodoId = todo.Id.ToString(),
                            TodoTitle = todo.Title,
                            ChildId = childId.ToString(),
                            ChildTitle = null,
                            IssueType = "MissingChild",
                            Description = $"ChildTodoId {childId} does not exist"
                        });
                    }
                    else if (child.ParentTodoId != todo.Id)
                    {
                        issues.Add(new ValidationIssue
                        {
                            TodoId = todo.Id.ToString(),
                            TodoTitle = todo.Title,
                            ChildId = child.Id.ToString(),
                            ChildTitle = child.Title,
                            IssueType = "IncorrectParentReference",
                            Description = $"Child '{child.Title}' does not reference this todo as parent (has ParentTodoId={child.ParentTodoId?.ToString() ?? "null"})"
                        });
                    }
                }
            }

            return Results.Ok(new
            {
                TotalTodos = todos.Count,
                IssuesFound = issues.Count,
                Issues = issues
            });
        })
        .WithName("ValidateTodoRelationships")
        .WithDescription("Validate all parent-child relationships for consistency");

        return app;
    }
}

public class ReconciliationResult
{
    public string TodoId { get; set; } = string.Empty;
    public string TodoTitle { get; set; } = string.Empty;
    public int CurrentChildCount { get; set; }
    public int ActualChildCount { get; set; }
    public List<Guid> MissingChildren { get; set; } = new();
    public List<Guid> ExtraChildren { get; set; } = new();
}

public class OrphanFixResult
{
    public string ParentId { get; set; } = string.Empty;
    public string ParentTitle { get; set; } = string.Empty;
    public string ChildId { get; set; } = string.Empty;
    public string? ChildTitle { get; set; }
    public string? PreviousParentId { get; set; }
    public string Action { get; set; } = string.Empty; // "Fixed", "WouldFix", "ChildNotFound"
    public string Message { get; set; } = string.Empty;
}

public class ValidationIssue
{
    public string TodoId { get; set; } = string.Empty;
    public string TodoTitle { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? ParentTitle { get; set; }
    public string? ChildId { get; set; }
    public string? ChildTitle { get; set; }
    public string IssueType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
