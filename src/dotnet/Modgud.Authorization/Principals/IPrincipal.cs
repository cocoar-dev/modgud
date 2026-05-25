namespace Modgud.Authorization.Principals;

/// <summary>
/// Anything that can be referenced by Id and carry permissions in the system —
/// a person, a group, a service account, a bot. Minimum contract for identity,
/// display, and lifecycle state.
/// <para>
/// Capabilities (containment, authentication, email) are expressed via additional
/// interfaces in this namespace: not every principal supports every capability.
/// Services consume those capability interfaces directly so they only ever touch
/// the subset they need.
/// </para>
/// </summary>
public interface IPrincipal
{
    Guid Id { get; }

    /// <summary>
    /// String-based principal-type discriminator. Defaults to the concrete class
    /// name (<see cref="object.GetType"/>.Name); concrete classes may override to
    /// keep a stable value across refactorings. Open set — new principal types
    /// (ServiceAccount, Webhook, Bot, …) can be declared without enum changes.
    /// </summary>
    string Type { get; }

    string DisplayName { get; }
    bool IsActive { get; }
    bool IsDeleted { get; }
}
