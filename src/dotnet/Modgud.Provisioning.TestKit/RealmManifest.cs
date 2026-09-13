using System.Text.Json.Nodes;

namespace Modgud.Provisioning.TestKit;

/// <summary>
/// Declarative description of a realm's complete configuration, posted to the Modgud
/// control-plane provisioning API. This is the client-side mirror of the server's manifest
/// contract. The JSON shape is what <c>POST /api/admin/realms/{slug}/apply</c> binds; the
/// round-trip is exercised end-to-end by the IdP repo's own provisioning tests so the two
/// sides can't silently drift.
///
/// <para>Authoring stays name-based — a group lists its members and roles by key, the way a
/// test wants to read. On the wire that is translated into the server's identity contract
/// (ADR 0024: a manifest identifies by id, never by name): every entity the kit declares
/// gets a document-local <c>#handle</c>, and every reference is rewritten to point at one.
/// So a realm this kit provisions can never adopt an entity that merely SHARES a name with
/// something already in the target. Set <c>Id</c> explicitly to pin a real, stable entity
/// id instead (stage → prod), or to address an entity that already exists there.</para>
///
/// <para>A manifest describes CONTENT only and carries no realm shell — the target realm is
/// named by the route, so the same file applies to any realm. Create the realm separately
/// with a <see cref="RealmSpec"/> (see
/// <see cref="ModgudProvisioningClient.ImportRealmAsync"/>).</para>
/// </summary>
public sealed record RealmManifest
{
    /// <summary>Optional raw realm-settings patch (self-registration, native grants, …).
    /// Left as a free-form JSON object so the kit doesn't have to mirror the full settings
    /// surface; <c>null</c> = no settings change.</summary>
    public JsonObject? Settings { get; init; }

    public List<RealmManifestApp> Apps { get; init; } = [];
    public List<RealmManifestApi> Apis { get; init; } = [];
    public List<RealmManifestScope> Scopes { get; init; } = [];
    public List<RealmManifestClient> Clients { get; init; } = [];
    public List<RealmManifestRole> Roles { get; init; } = [];
    public List<RealmManifestUser> Users { get; init; } = [];
    public List<RealmManifestGroup> Groups { get; init; } = [];
}

/// <summary>The realm shell — the payload of <c>POST /api/admin/realms</c>. Deliberately NOT
/// part of <see cref="RealmManifest"/>: slug, routing domains and primary domain are
/// deployment identity, and keeping them out of the manifest is what makes a manifest
/// portable between realms and environments.</summary>
public sealed record RealmSpec
{
    public required string Slug { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[]? Domains { get; init; }
    public string? PrimaryDomain { get; init; }
    public InitialAdmin InitialAdmin { get; init; } = new();
}

/// <summary>The realm's first admin, issued as a pending admin invite by the create-realm
/// call. Optional — a manifest can equally provision admins outright via
/// <see cref="RealmManifest.Users"/> + <see cref="RealmManifest.Groups"/>.</summary>
public sealed record InitialAdmin
{
    public string UserName { get; init; } = "admin";
    public string Email { get; init; } = "admin@example.test";
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}

/// <summary>
/// A cross-reference to another entity in the manifest. Author it as a plain string — the
/// kit rewrites it before sending (see <see cref="RealmManifest"/>). On the wire it is
/// either a <c>"#handle"</c> for an entity this manifest creates, or
/// <c>{ "Key": "...", "Id": "..." }</c> for one addressed by a real id; a bare name is
/// refused by the server, which is why the kit never sends one.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ManifestRefJsonConverter))]
public sealed record ManifestRef
{
    public string? Key { get; init; }
    public string? Id { get; init; }

    public static implicit operator ManifestRef(string value) => new() { Key = value };

    public override string ToString() => Key ?? Id ?? string.Empty;
}

/// <summary>Writes the two wire forms the server accepts. A real id MUST go out as the
/// object form: a bare string without '#' reads back as a name, and the server refuses
/// names outright.</summary>
public sealed class ManifestRefJsonConverter : System.Text.Json.Serialization.JsonConverter<ManifestRef>
{
    public override ManifestRef Read(ref System.Text.Json.Utf8JsonReader reader, Type t, System.Text.Json.JsonSerializerOptions o)
        => reader.TokenType == System.Text.Json.JsonTokenType.String
            ? new ManifestRef { Key = reader.GetString() }
            : throw new System.Text.Json.JsonException("A reference reads as a string here.");

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ManifestRef value, System.Text.Json.JsonSerializerOptions o)
    {
        if (value.Id is null) { writer.WriteStringValue(value.Key); return; }
        if (value.Key is null && value.Id.StartsWith('#')) { writer.WriteStringValue(value.Id); return; }
        writer.WriteStartObject();
        if (value.Key is not null) writer.WriteString("Key", value.Key);
        writer.WriteString("Id", value.Id);
        writer.WriteEndObject();
    }
}

public sealed record RealmManifestPermission(string Resource, string Action, string? Description = null);

public sealed record RealmManifestApp
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

public sealed record RealmManifestApi
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Scopes { get; init; } = [];
    public List<RealmManifestPermission> Permissions { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? AllowDynamicRegistration { get; init; }
}

public sealed record RealmManifestScope
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public List<string> Resources { get; init; } = [];
    public List<string> UserClaims { get; init; } = [];
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? Required { get; init; }
    public bool? Emphasize { get; init; }
    public bool? ShowInDiscoveryDocument { get; init; }
}

public sealed record RealmManifestClient
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public required string ClientId { get; init; }
    public string? DisplayName { get; init; }
    public required string ClientType { get; init; }
    public string? ClientSecret { get; init; }
    public List<string> RedirectUris { get; init; } = [];
    public List<string> PostLogoutRedirectUris { get; init; } = [];
    public List<string> Scopes { get; init; } = [];
    public List<string> AllowedGrantTypes { get; init; } = [];
    public List<string> Apps { get; init; } = [];
    public List<string> Roles { get; init; } = [];
    public string? WebAuthnRpId { get; init; }
    // Nullable = surgical patch: omitted = no change on apply / default on create.
    public bool? Enabled { get; init; }
    public bool? RequireConsent { get; init; }
}

public sealed record RealmManifestRole
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public string? Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? App { get; init; }
    public bool IsRealmAdmin { get; init; }
    public List<RealmManifestPermission> Permissions { get; init; } = [];
}

public sealed record RealmManifestUser
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public string? Key { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? Acronym { get; init; }
    public required string Email { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool EmailConfirmed { get; init; }
    /// <summary>Null = unchanged (true on create). False is the kill switch: the apply
    /// revokes the user's grants, sessions and cookie after it commits.</summary>
    public bool? IsActive { get; init; }
    /// <summary>Per-user 2FA grace-period override in days; null = unchanged (the realm
    /// default on create). The kit cannot express "clear an existing override" — use the
    /// admin API for that.</summary>
    public int? GracePeriodDaysOverride { get; init; }
    /// <summary>Null = unchanged (false on create). True exempts the user from 2FA
    /// enforcement entirely.</summary>
    public bool? TwoFactorExempt { get; init; }
}

public sealed record RealmManifestGroup
{
    /// <summary>Optional entity identity. Leave it null and the kit assigns a
    /// document-local <c>#handle</c> so this entry always CREATES; set a real id
    /// (ShortGuid or Guid) to pin it across environments or to address an entity the
    /// target realm already has. See <see cref="RealmManifest"/>.</summary>
    public string? Id { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<ManifestRef> Members { get; init; } = [];
    public List<ManifestRef> Roles { get; init; } = [];
    public string MembershipMode { get; init; } = "Manual";
    public string? MembershipScript { get; init; }
    public string? Email { get; init; }
    public string EmailMode { get; init; } = "Shared";
    public List<string>? BoundTo { get; init; }
    public bool ExternallyDrivable { get; init; }
}
