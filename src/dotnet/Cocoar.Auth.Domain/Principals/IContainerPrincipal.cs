namespace Cocoar.Auth.Domain.Principals;

/// <summary>
/// A principal that contains other principals. Membership is by PrincipalId —
/// a container can contain Persons, other Containers (nested groups), or any
/// other principal type.
/// </summary>
public interface IContainerPrincipal : IPrincipal
{
    IReadOnlyList<Guid> MemberIds { get; }
}
