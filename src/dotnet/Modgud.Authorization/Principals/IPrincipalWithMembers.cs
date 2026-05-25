namespace Modgud.Authorization.Principals;

/// <summary>
/// A principal that contains other principals by id. Membership is polymorphic —
/// a container can hold persons, other containers (nested groups), service
/// accounts, or anything else implementing <see cref="IPrincipal"/>.
/// </summary>
public interface IPrincipalWithMembers : IPrincipal
{
    IReadOnlyList<Guid> MemberIds { get; }
}
