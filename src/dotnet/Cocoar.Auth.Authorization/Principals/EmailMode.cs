namespace Cocoar.Auth.Authorization.Principals;

/// <summary>
/// How email sends targeted at a principal are resolved.
/// </summary>
public enum EmailMode
{
    /// <summary>Direct — send to the principal's own address.</summary>
    Shared = 0,

    /// <summary>Recursive — resolve container members to their emails and send to each.</summary>
    ExpandToMembers = 1,
}
