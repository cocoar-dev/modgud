using System.Text.Json;

namespace TimeToDo.Authentication.Domain.ExternalAuth;

/// <summary>
/// Admin-configurable external identity provider. One record per IdP-instance
/// (e.g. two Entra tenants get two records). Event-sourced; inline projection
/// in <c>IdpConfigProjection</c>.
/// <para>
/// <b>Secret handling:</b> The client secret is never stored in clear text and
/// never appears in event payloads. Events only record that a rotation happened
/// (metadata), while the encrypted bytes live on this document and are written
/// in a side-channel at event-apply time (Marten allows this because the
/// projection is inline and has access to the raw encrypted payload on the
/// rotation event).
/// </para>
/// </summary>
public class IdpConfig
{
    public Guid Id { get; set; }

    /// <summary>Flavor key (see <see cref="IdpFlavor"/>). Immutable after creation.</summary>
    public string Flavor { get; set; } = IdpFlavor.GenericOidc;

    /// <summary>Admin-facing name + login-page button label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    // ── OIDC basics ──────────────────────────────────────────────────
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// DataProtection-encrypted bytes of the client secret. Never read directly
    /// by business code — <c>IdpSecretStore</c> (Phase 2) handles encrypt/decrypt.
    /// </summary>
    public byte[]? ClientSecretEncrypted { get; set; }

    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    // ── User-Update Script ───────────────────────────────────────────
    /// <summary>
    /// JsEval script body with signature <c>(claims) => ({ firstname, lastname, email, acronym })</c>.
    /// Runs on every successful login through this IdP: the returned object is
    /// interpreted as a <b>patch</b> against the TimeToDo user record — only
    /// Firstname, Lastname, Email, and Acronym are writable. A field that is
    /// <c>undefined</c> (or missing) means "do not touch"; an explicit
    /// <c>null</c> means "clear the value".
    /// <para>
    /// Used both for JIT user creation (first login — builds the user) and for
    /// return logins (updates properties if they changed upstream). The script
    /// never writes persistent groups/roles — membership stays under
    /// TimeToDo's control (for IdP-driven membership use SCIM, planned).
    /// </para>
    /// </summary>
    public string UserUpdateScript { get; set; } = string.Empty;

    // ── Script snapshot persistence (on ExternalIdentityLink) ────────
    /// <summary>
    /// When <c>true</c>, each successful login persists the raw IdP claims
    /// alongside the script's output. Default comes from the flavor
    /// (Enterprise flavors default to <c>true</c>, consumer flavors to <c>false</c>).
    /// PII-sensitive — admin can toggle per-config.
    /// </summary>
    public bool StoreRawClaims { get; set; }

    /// <summary>Optional retention cap on the raw claims snapshot. <c>null</c> = keep as long as the link exists.</summary>
    public int? RawClaimsRetentionDays { get; set; }

    // ── Linking policy ───────────────────────────────────────────────
    /// <summary>Auto-create a new TimeToDo user for an unseen subject.</summary>
    public bool AutoCreateUsers { get; set; }

    /// <summary>Allow users to add this IdP as an additional link from their profile.</summary>
    public bool AllowLinking { get; set; } = true;

    /// <summary>
    /// Dangerous opt-in: if an unseen subject arrives with an email that matches
    /// an existing TimeToDo user, auto-link instead of refusing. Enable only for
    /// tenant-controlled enterprise IdPs.
    /// </summary>
    public bool TrustForEmailLink { get; set; }

    /// <summary>Optional email-domain allowlist (e.g. <c>["acme.com"]</c>). <c>null</c> = no filter.</summary>
    public List<string>? AllowedEmailDomains { get; set; }

    // ── Branding ─────────────────────────────────────────────────────
    public string? IconName { get; set; }
    public string? ButtonColorHex { get; set; }

    // ── Flavor-specific payload ──────────────────────────────────────
    /// <summary>
    /// Flavor-specific config fields (e.g. <c>{ "TenantId": "..." }</c> for Entra,
    /// <c>{ "MetadataUri": "..." }</c> for Generic OIDC). Shape is owned by the
    /// flavor class; document stores it as a JSON tree so adding a flavor-field
    /// doesn't require a schema change.
    /// </summary>
    public JsonDocument? FlavorData { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
