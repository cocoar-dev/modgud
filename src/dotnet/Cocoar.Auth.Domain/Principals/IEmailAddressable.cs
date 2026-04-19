namespace Cocoar.Auth.Domain.Principals;

/// <summary>
/// A principal that has an email address. For humans, this is the personal
/// address. For groups, this may be a shared mailbox (<see cref="EmailMode.Shared"/>)
/// or a notification-expand trigger (<see cref="EmailMode.ExpandToMembers"/>).
/// </summary>
public interface IEmailAddressable : IPrincipal
{
    string? Email { get; }
    EmailMode EmailMode { get; }
}

/// <summary>
/// How email sends targeted at this principal are resolved.
/// </summary>
public enum EmailMode
{
    /// <summary>Direct — send to <see cref="IEmailAddressable.Email"/>.</summary>
    Shared = 0,
    /// <summary>Recursive — resolve container members to their emails and send to each.</summary>
    ExpandToMembers = 1
}
