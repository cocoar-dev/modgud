namespace Modgud.Domain.OAuth.Applications;

public record OAuthApplicationCreated(
    Guid ApplicationId,
    string ClientId,
    string? DisplayName,
    string? ClientType,
    string? ConsentType,
    string? ApplicationType,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Requirements);

public record OAuthApplicationDisplayNameChanged(Guid ApplicationId, string? DisplayName);

/// <summary>
/// Legacy single-app link event. Kept for stream replay compat — early
/// Phase-3 commits emitted this; the projection still applies it as
/// "set AppIds to the singleton (or empty)". New writes emit
/// <see cref="OAuthApplicationAppIdsChanged"/> instead.
/// </summary>
public record OAuthApplicationAppIdChanged(Guid ApplicationId, Guid? AppId);

/// <summary>
/// Sets the n:m link between this OAuth client and Applications. The list
/// can be empty (realm-wide / unassigned), have one entry (typical web
/// SPA bound to a single app), or many (a frontend that bundles multiple
/// resource servers — Keycloak-style <c>resource_access</c>). Realm-admin
/// endpoints validate that every <see cref="AppIds"/> entry references a
/// non-deleted <c>App</c> at append time.
/// </summary>
public record OAuthApplicationAppIdsChanged(Guid ApplicationId, IReadOnlyList<Guid> AppIds);

public record OAuthApplicationClientTypeChanged(Guid ApplicationId, string? ClientType);

public record OAuthApplicationConsentTypeChanged(Guid ApplicationId, string? ConsentType);

public record OAuthApplicationRedirectUrisChanged(Guid ApplicationId, IReadOnlyList<string> RedirectUris);

public record OAuthApplicationPostLogoutRedirectUrisChanged(Guid ApplicationId, IReadOnlyList<string> PostLogoutRedirectUris);

public record OAuthApplicationPermissionsChanged(Guid ApplicationId, IReadOnlyList<string> Permissions);

public record OAuthApplicationRequirementsChanged(Guid ApplicationId, IReadOnlyList<string> Requirements);

public record OAuthApplicationSettingsChanged(Guid ApplicationId, IReadOnlyDictionary<string, string> Settings);

public record OAuthApplicationDisplayNamesChanged(Guid ApplicationId, IReadOnlyDictionary<string, string> DisplayNames);

public record OAuthApplicationPropertiesChanged(Guid ApplicationId, IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Sets (or clears with null) the link from this OAuth client to a
/// ServiceAccount. The token endpoint's <c>client_credentials</c> branch
/// reads this to resolve <c>sub = SA.Id</c> instead of the OAuth client's
/// own id, so audit logs and the Group → Role → Permission chain treat
/// machine principals consistently with humans. See
/// <c>dev-docs/future-features/service-account-credentials.md</c>
/// for the design.
/// </summary>
public record OAuthApplicationServiceAccountLinkChanged(Guid ApplicationId, Guid? ServiceAccountId);

public record OAuthApplicationDeleted(Guid ApplicationId);
