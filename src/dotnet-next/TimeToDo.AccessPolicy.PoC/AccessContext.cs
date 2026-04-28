namespace TimeToDo.AccessPolicy.PoC;

/// <summary>
/// Context available to access policy scripts.
/// Passed into Jint so the script can use ctx.UserId, ctx.ManagedCustomerIds, etc.
/// </summary>
public class AccessContext
{
    public Guid UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public List<string> Permissions { get; init; } = [];
    public List<Guid> ManagedCustomerIds { get; init; } = [];

    public bool HasPermission(string permission)
    {
        if (Permissions.Contains("app:admin"))
            return true;
        return Permissions.Contains(permission);
    }
}
