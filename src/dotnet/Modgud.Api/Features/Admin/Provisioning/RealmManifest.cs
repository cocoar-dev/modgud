using Modgud.Api.Features.Users.Commands;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// A declarative description of a realm's complete configuration. Applied in-process
/// by <see cref="RealmManifestApplier"/>, which maps each section onto the SAME
/// canonical application operation the admin UI/API uses — it never reimplements a
/// mutation. v1 carries the realm shell + settings + OAuth (apis/scopes/clients) +
/// users; apps, roles, groups and login providers are added incrementally.
///
/// <para>The sections reuse the existing Create DTOs verbatim, so the manifest schema
/// stays in lockstep with the operations it drives. Cross-references that need
/// server-generated ids (app linkage, role/group membership) arrive with those later
/// sections.</para>
/// </summary>
public sealed record RealmManifest
{
    /// <summary>Realm shell + initial admin (see <see cref="CreateRealmDto"/>).</summary>
    public required CreateRealmDto Realm { get; init; }

    /// <summary>Optional realm settings patch (self-registration, native grants, ...).</summary>
    public UpdateRealmSettingsDto? Settings { get; init; }

    public List<CreateOAuthApiDto> Apis { get; init; } = [];
    public List<CreateOAuthScopeDto> Scopes { get; init; } = [];
    public List<CreateOAuthClientDto> Clients { get; init; } = [];
    public List<RealmManifestUser> Users { get; init; } = [];
}

/// <summary>A user to provision into the realm (maps to <see cref="CreateUserCommand"/>).</summary>
public sealed record RealmManifestUser
{
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? Acronym { get; init; }
    public required string Email { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool EmailConfirmed { get; init; }

    public CreateUserCommand ToCommand() =>
        new(Firstname, Lastname, Acronym, Email, UserName ?? string.Empty, Password, EmailConfirmed);
}

/// <summary>The outcome of a successful import.</summary>
public sealed record RealmImportResult
{
    public required string Slug { get; init; }
    public required string PrimaryDomain { get; init; }

    /// <summary>
    /// Plaintext secrets of the confidential clients created during the import
    /// (clientId → secret). Secrets are only returned at create time, so they are
    /// surfaced here for a test-kit / caller to use without a separate fetch.
    /// </summary>
    public Dictionary<string, string> ClientSecrets { get; init; } = [];
}
