namespace TimeToDo.Authorization.Principals;

/// <summary>
/// How a <see cref="Group"/>'s member list is maintained.
/// </summary>
public enum MembershipMode
{
    /// <summary>Admin manages <c>MemberIds</c> directly.</summary>
    Manual = 0,

    /// <summary>
    /// A JavaScript predicate script picks members dynamically from the principal
    /// directory. <see cref="Group.MembershipScript"/> holds the source; the
    /// auto-membership recalculator materialises <c>MemberIds</c> from it.
    /// </summary>
    Auto = 1,
}
