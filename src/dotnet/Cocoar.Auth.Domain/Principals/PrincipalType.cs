namespace Cocoar.Auth.Domain.Principals;

/// <summary>
/// String-based principal type discriminators. Open set — new principal types
/// (ServiceAccount, Webhook, Bot, ...) can be added later by declaring a new
/// constant and registering a projection handler for that type's events.
/// <para>
/// <see cref="Person"/> denotes a natural person (real human with a login, name,
/// email). Distinct from "user" on purpose — "user" is a role (anyone operating
/// the system, including service accounts), not an identity type.
/// </para>
/// </summary>
public static class PrincipalType
{
    public const string Person = "Person";
    public const string Group = "Group";
    // Future: ServiceAccount, Webhook, Bot, ...
}
