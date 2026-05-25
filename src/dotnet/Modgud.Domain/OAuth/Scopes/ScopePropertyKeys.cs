namespace Modgud.Domain.OAuth.Scopes;

/// <summary>Custom property keys stored on scope Properties (JSON-element values).</summary>
public static class ScopePropertyKeys
{
    public const string Enabled = "cocoar:enabled";
    public const string Required = "cocoar:required";
    public const string Emphasize = "cocoar:emphasize";
    public const string ShowInDiscoveryDocument = "cocoar:show_in_discovery_document";
    public const string UserClaims = "cocoar:user_claims";

    /// <summary>Boolean — when <c>true</c>, this scope is requestable by
    /// clients minted via Dynamic Client Registration. Off by default.
    /// One half of the triple opt-in: gates which capabilities DCR
    /// clients can ever ask for, regardless of their resource target.
    /// Stops a DCR client from requesting <c>tenant.admin.*</c> and
    /// relying on a habit-click consent.</summary>
    public const string AllowDynamicRegistrationClients = "cocoar:allow_dynamic_registration_clients";
}

/// <summary>
/// Constants for the system-managed scopes seeded into every realm — the OIDC
/// core set (<c>openid</c>, <c>email</c>, <c>profile</c>, <c>phone</c>,
/// <c>address</c>, <c>offline_access</c>) plus the two Cocoar-defined
/// authorization-claim gates (<c>roles</c>, <c>permissions</c>).
///
/// <para>Membership in <see cref="All"/> drives two pieces of behaviour:</para>
/// <list type="bullet">
///   <item>The admin UI dims these rows and hides the delete affordance —
///   they're contract-bound by the runtime, renaming or removing them would
///   break clients (e.g. <c>scope=permissions</c> wouldn't resolve).</item>
///   <item><see cref="Modgud.Application.Errors.OAuthErrors.CannotModifyStandardScope"/>
///   and <c>CannotDeleteStandardScope</c> reject edits on these names at
///   the service layer regardless of UI state.</item>
/// </list>
/// </summary>
public static class StandardScopes
{
    public const string OpenId = "openid";
    public const string Email = "email";
    public const string Profile = "profile";
    public const string Phone = "phone";
    public const string Address = "address";
    public const string Roles = "roles";
    public const string OfflineAccess = "offline_access";

    /// <summary>
    /// Cocoar-specific scope that gates emission of the per-audience
    /// <c>resource_access[…].permissions</c> array in UserInfo. Not part of
    /// OIDC core; modelled after the same per-scope-per-claim opt-in pattern
    /// that <see cref="Roles"/> uses for role names. Static-registered so it
    /// appears in <c>scopes_supported</c> and is offered on the consent screen.
    /// </summary>
    public const string Permissions = "permissions";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        OpenId, Email, Profile, Phone, Address, Roles, OfflineAccess, Permissions,
    };

    public static bool IsStandard(string? name) =>
        name is not null && All.Contains(name);
}
