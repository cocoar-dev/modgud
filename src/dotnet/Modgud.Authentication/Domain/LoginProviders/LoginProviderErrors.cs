using ErrorOr;

namespace Modgud.Authentication.Domain.LoginProviders;

/// <summary>
/// Single source of truth for the LoginProvider-related errors that surface in
/// multiple places (admin commands + runtime auth-flow code). Keeping the codes
/// centralized so the frontend can rely on stable identifiers.
/// </summary>
public static class LoginProviderErrors
{
    /// <summary>
    /// Returned wherever the runtime or admin layer is asked to act on a
    /// LoginProvider whose <see cref="LoginProviderType"/> is unsupported by
    /// the requested surface. For example, the OIDC start route rejects SAML
    /// because SAML uses its own SP-initiated route; LDAP/Kerberos are not
    /// implemented. Admin and runtime paths share the same stable error shape.
    /// </summary>
    public static Error TypeNotSupported(LoginProviderType type) => Error.Validation(
        code: "LoginProvider.TypeNotSupported",
        description: $"LoginProvider type '{type}' is not yet supported.");

    /// <summary>
    /// The seeded Internal LoginProvider (<c>IsBuiltIn=true</c>) is hard-blocked
    /// from edits — the realm seeder owns its shape. Returned by
    /// <c>UpdateLoginProviderCommand</c> and any future write commands.
    /// </summary>
    public static Error InternalNotEditable(string displayName) => Error.Conflict(
        code: "LoginProvider.InternalNotEditable",
        description: $"The built-in Internal login provider '{displayName}' is not editable.");

    /// <summary>
    /// At most one Internal LoginProvider per realm — the seeder writes it on
    /// realm creation. Admin <c>Create</c> with <c>Type=Internal</c> is rejected
    /// when an Internal entry already exists.
    /// </summary>
    public static Error InternalAlreadyExists() => Error.Conflict(
        code: "LoginProvider.InternalAlreadyExists",
        description: "An Internal login provider already exists in this realm.");
}
