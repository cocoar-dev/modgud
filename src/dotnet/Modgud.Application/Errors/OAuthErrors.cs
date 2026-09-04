using ErrorOr;

namespace Modgud.Application.Errors;

public static class OAuthErrors
{
    public static Error ClientIdAlreadyExists(string clientId) => Error.Conflict(
        code: "OAuth.ClientIdAlreadyExists",
        description: $"An OAuth client with ID '{clientId}' already exists.");

    public static Error ClientNotFound(string id) => Error.NotFound(
        code: "OAuth.ClientNotFound",
        description: $"OAuth client with ID '{id}' was not found.");

    public static Error InvalidClientType(string clientType) => Error.Validation(
        code: "OAuth.InvalidClientType",
        description: $"Invalid client type '{clientType}'. Must be 'public' or 'confidential'.");

    public static Error InvalidConsentType(string consentType) => Error.Validation(
        code: "OAuth.InvalidConsentType",
        description: $"Invalid consent type '{consentType}'. Must be 'explicit', 'implicit', or 'external'.");

    public static Error UnsupportedGrantType(string grantType) => Error.Validation(
        code: "OAuth.UnsupportedGrantType",
        description: $"Grant type '{grantType}' is not supported. Allowed: authorization_code, "
                   + "client_credentials, refresh_token, urn:ietf:params:oauth:grant-type:device_code, "
                   + "and the native urn:cocoar:* grants. The OAuth 2.1-removed 'implicit' and 'password' "
                   + "grants are rejected.");

    public static Error InvalidBackChannelLogoutUri(string value, string reason) => Error.Validation(
        code: "OAuth.InvalidBackChannelLogoutUri",
        description: $"Invalid back-channel logout URI '{value}': {reason}");

    public static Error InvalidWebAuthnRpId(string value) => Error.Validation(
        code: "OAuth.InvalidWebAuthnRpId",
        description: $"Invalid WebAuthn RP ID '{value}'. Must be a bare hostname (e.g. 'app.example.com') "
                   + "— no scheme, port, path, or whitespace.");

    public static Error CannotRegenerateSecretForPublicClient => Error.Validation(
        code: "OAuth.CannotRegenerateSecretForPublicClient",
        description: "Cannot regenerate secret for a public client. Only confidential clients have secrets.");

    public static Error ScopeNameAlreadyExists(string name) => Error.Conflict(
        code: "OAuth.ScopeNameAlreadyExists",
        description: $"An OAuth scope with name '{name}' already exists.");

    public static Error ScopeNotFound(string id) => Error.NotFound(
        code: "OAuth.ScopeNotFound",
        description: $"OAuth scope with ID '{id}' was not found.");

    public static Error CannotModifyStandardScope(string name) => Error.Validation(
        code: "OAuth.CannotModifyStandardScope",
        description: $"Cannot modify the standard scope '{name}'.");

    public static Error CannotDeleteStandardScope(string name) => Error.Validation(
        code: "OAuth.CannotDeleteStandardScope",
        description: $"Cannot delete the standard scope '{name}'.");

    public static Error ApiNameAlreadyExists(string name) => Error.Conflict(
        code: "OAuth.ApiNameAlreadyExists",
        description: $"An API with name '{name}' already exists.");

    public static Error ApiNotFound(string id) => Error.NotFound(
        code: "OAuth.ApiNotFound",
        description: $"API with ID '{id}' was not found.");

    public static Error InvalidServiceAccountId(string id) => Error.Validation(
        code: "OAuth.InvalidServiceAccountId",
        description: $"LinkedServiceAccountId '{id}' is not a valid Guid or ShortGuid.");

    public static Error ServiceAccountNotFound(string id) => Error.Validation(
        code: "OAuth.ServiceAccountNotFound",
        description: $"ServiceAccount '{id}' not found or deleted.");

    public static Error ServiceAccountLinkModesAreMutuallyExclusive => Error.Validation(
        code: "OAuth.ServiceAccountLinkModesAreMutuallyExclusive",
        description: "Provide either LinkedServiceAccountId or NewServiceAccount, not both.");

    public static Error InvalidNewServiceAccountName => Error.Validation(
        code: "OAuth.InvalidNewServiceAccountName",
        description: "The new ServiceAccount account name must be 2-64 characters and contain only lowercase letters, digits, dots, hyphens, or underscores.");

    public static Error ServiceAccountNameAlreadyExists(string accountName) => Error.Conflict(
        code: "OAuth.ServiceAccountNameAlreadyExists",
        description: $"Account name '{accountName}' is already used by a person or ServiceAccount.");

    public static Error ClientCredentialsRequiresServiceAccountLink => Error.Validation(
        code: "OAuth.ClientCredentialsRequiresServiceAccountLink",
        description: "A client with the 'client_credentials' grant must reference LinkedServiceAccountId or include NewServiceAccount.");

    public static Error InvalidPositionId(string id) => Error.Validation(
        code: "OAuth.InvalidPositionId",
        description: $"LinkedPositionPrincipalId '{id}' is not a valid Guid or ShortGuid.");

    public static Error PositionNotFound(string id) => Error.Validation(
        code: "OAuth.PositionNotFound",
        description: $"Position '{id}' not found or deleted.");

    public static Error InvalidNewPositionName => Error.Validation(
        code: "OAuth.InvalidNewPositionName",
        description: "The new Position account name must be 2-64 characters and contain only lowercase letters, digits, dots, hyphens, or underscores.");

    public static Error PositionNameAlreadyExists(string accountName) => Error.Conflict(
        code: "OAuth.PositionNameAlreadyExists",
        description: $"Account name '{accountName}' is already used by a person, ServiceAccount, or Position.");

    public static Error PositionLinkModesAreMutuallyExclusive => Error.Validation(
        code: "OAuth.PositionLinkModesAreMutuallyExclusive",
        description: "Provide either LinkedPositionPrincipalId or NewPosition, not both.");

    public static Error StaffingGrantRequiresPositionLink => Error.Validation(
        code: "OAuth.StaffingGrantRequiresPositionLink",
        description: "A client with the staffing grant must reference LinkedPositionPrincipalId or include NewPosition — the terminal counterpart of the client_credentials rule.");

    public static Error PositionLinkRequiresStaffingGrant => Error.Validation(
        code: "OAuth.PositionLinkRequiresStaffingGrant",
        description: "A position-linked client must carry the terminal grants (device_code + refresh_token + staffing); it is a shared-terminal client, not a general-purpose one.");

    public static Error TerminalDisplayNameRequired => Error.Validation(
        code: "OAuth.TerminalDisplayNameRequired",
        description: "A position-linked client needs TerminalDisplayName — it names the slot the device serves.");

    public static Error PositionTerminalsDisabled(string accountName) => Error.Validation(
        code: "OAuth.PositionTerminalsDisabled",
        description: $"Position '{accountName}' has terminal use switched off. Enable it before attaching terminal clients.");

    public static Error ServiceAccountLinkRequiresClientCredentialsOnly => Error.Validation(
        code: "OAuth.ServiceAccountLinkRequiresClientCredentialsOnly",
        description: "A ServiceAccount-linked client must use only the 'client_credentials' grant. User-flow grants (authorization_code, refresh_token, device_code) are forbidden — strict separation between user-flow and machine-to-machine clients.");

    public static Error CannotMutateServiceAccountManagedClient(string clientId) => Error.Validation(
        code: "OAuth.CannotMutateServiceAccountManagedClient",
        description: $"OAuth client '{clientId}' is owned by a ServiceAccount. Mutations must go through the SA-scoped credentials endpoints; the standard admin PUT is read-only for SA-managed clients.");

    // ── MG-FT-03 — terminal-managed clients ───────────────────────────────

    public static Error InvalidPositionTerminalClient(string rule) => Error.Validation(
        code: "OAuth.InvalidPositionTerminalClient",
        description: $"Invalid position-terminal client profile: {rule}");

    public static Error CannotMutateTerminalManagedClient(string clientId) => Error.Validation(
        code: "OAuth.CannotMutateTerminalManagedClient",
        description: $"OAuth client '{clientId}' is owned by a position terminal. Mutations must go through the position-terminal endpoints (/api/position/{{id}}/terminals); the standard admin surface is read-only for terminal-managed clients.");
}
