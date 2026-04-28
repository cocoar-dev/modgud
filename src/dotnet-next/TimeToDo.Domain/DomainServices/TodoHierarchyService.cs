using ErrorOr;
using TimeToDo.Domain.Entities;
using TimeToDo.Domain.Errors;

namespace TimeToDo.Domain.DomainServices;

/// <summary>
/// Domain service for managing Todo parent-child hierarchy rules.
/// </summary>
public class TodoHierarchyService
{
    /// <summary>
    /// Validates and executes the operation to make a todo a child of another.
    /// </summary>
    /// <param name="child">The todo that will become a child</param>
    /// <param name="parent">The todo that will become the parent</param>
    /// <returns>Success or error</returns>
    public ErrorOr<Success> MakeChildOf(Todo child, Todo parent)
    {
        // Cannot be own parent
        if (child.Id == parent.Id)
            return DomainErrors.Todo.CannotBeOwnParent;

        // Child cannot have children (would create nesting > 1 level)
        var canBecomeSubtodo = child.CanBecomeSubtodo();
        if (canBecomeSubtodo.IsError)
            return canBecomeSubtodo.Errors;

        // Parent cannot already be a subtodo (would create nesting > 1 level)
        var canAcceptChild = parent.CanAcceptChild();
        if (canAcceptChild.IsError)
            return canAcceptChild.Errors;

        // Prevent circular reference (parent cannot be a child of the child)
        if (parent.ParentTodoId == child.Id)
            return DomainErrors.Todo.CircularReference;

        // If child already has a different parent, it will be removed by the caller
        // Set the new parent relationship
        child.SetParent(parent.Id, parent.CustomerId);
        parent.AddChild(child.Id);

        return Result.Success;
    }

    /// <summary>
    /// Removes the parent-child relationship.
    /// </summary>
    /// <param name="child">The child todo</param>
    /// <param name="parent">The parent todo</param>
    public void RemoveFromParent(Todo child, Todo parent)
    {
        child.ClearParent();
        parent.RemoveChild(child.Id);
    }

    /// <summary>
    /// Converts a subtodo to a root todo.
    /// </summary>
    /// <param name="todo">The todo to convert</param>
    /// <param name="formerParent">The former parent (can be null if already a root)</param>
    public void ConvertToRootTodo(Todo todo, Todo? formerParent)
    {
        if (formerParent != null)
        {
            todo.ClearParent();
            formerParent.RemoveChild(todo.Id);
        }
    }

    /// <summary>
    /// Handles orphaning children when a parent is deleted.
    /// Children become root todos but keep their inherited customer.
    /// </summary>
    /// <param name="children">The children to orphan</param>
    public void OrphanChildren(IEnumerable<Todo> children)
    {
        foreach (var child in children)
        {
            child.ClearParent();
        }
    }
}
