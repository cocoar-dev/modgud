namespace Cocoar.Auth.Domain.Principals;

/// <summary>
/// Anything that can be referenced by Id in the system: a human user, a group,
/// a service account, a bot. Minimum contract for cross-type lookup and display.
/// <para>
/// Capabilities (auth, containment, email) are expressed via additional
/// interfaces below — not all principals support all capabilities.
/// </para>
/// </summary>
public interface IPrincipal
{
    Guid Id { get; }
    string DisplayName { get; }
    string Type { get; }        // PrincipalType.Person | PrincipalType.Group | ...
    bool IsActive { get; }
}
