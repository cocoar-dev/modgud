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

    public static Error ServiceAccountLinkRequiresClientCredentialsOnly => Error.Validation(
        code: "OAuth.ServiceAccountLinkRequiresClientCredentialsOnly",
        description: "A ServiceAccount-linked client must use only the 'client_credentials' grant. User-flow grants (authorization_code, refresh_token, device_code) are forbidden — strict separation between user-flow and machine-to-machine clients.");

    public static Error CannotMutateServiceAccountManagedClient(string clientId) => Error.Validation(
        code: "OAuth.CannotMutateServiceAccountManagedClient",
        description: $"OAuth client '{clientId}' is owned by a ServiceAccount. Mutations must go through the SA-scoped credentials endpoints; the standard admin PUT is read-only for SA-managed clients.");
}
