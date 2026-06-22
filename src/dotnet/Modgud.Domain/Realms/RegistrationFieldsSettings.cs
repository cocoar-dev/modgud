namespace Modgud.Domain.Realms;

/// <summary>
/// How strictly a single identity field is enforced when a user account is
/// created (admin-create, self-registration, native passwordless registration,
/// federation JIT). Uniform tri-state, applied per configurable field.
///
/// <para><c>Optional</c> is deliberately the zero value so that
/// <c>default(FieldRequirement)</c> — and any stored doc missing the field —
/// reads as today's lenient behaviour (zero behaviour change).</para>
/// </summary>
public enum FieldRequirement
{
    /// <summary>Field is not collected. The SPA hides the input; for
    /// <c>Username</c> the value is always derived from the email.</summary>
    Optional = 0,

    /// <summary>Field is hidden / not collected. For <c>Username</c> this means
    /// "always equal to the email"; for the name fields it means the field is
    /// never captured.</summary>
    Off = 1,

    /// <summary>Field must be supplied with a non-empty value; an empty value is
    /// rejected on every creation path.</summary>
    Required = 2,
}

/// <summary>
/// Per-realm policy for which identity fields are required when a user account
/// is created. A sub-record on the tenant-DB
/// <see cref="RealmSettings.RealmSettings"/> aggregate (stored as JSONB — adding
/// fields needs no schema migration), owned by the realm-admin and overridable
/// per-Application (ADR-0011 cascade) via
/// <c>ApplicationRegistrationFieldsOverrides</c>.
///
/// <para>Email is always required and is therefore NOT represented here — it is
/// the anchor every other field is derived against. The three configurable
/// fields each default to <see cref="FieldRequirement.Optional"/>, which is
/// exactly today's behaviour, so a realm that never touches this section behaves
/// as before (zero behaviour change). Null on the parent = never configured;
/// callers read it as <see cref="Defaults"/>.</para>
/// </summary>
public record RegistrationFieldsSettings
{
    /// <summary>Whether a distinct username must be supplied.
    /// <see cref="FieldRequirement.Off"/> = the username is always the email;
    /// <see cref="FieldRequirement.Optional"/> = a username may be supplied,
    /// empty falls back to the email; <see cref="FieldRequirement.Required"/> =
    /// a non-empty username must be supplied.</summary>
    public FieldRequirement Username { get; init; } = FieldRequirement.Optional;

    /// <summary>Whether the given name must be supplied.</summary>
    public FieldRequirement Firstname { get; init; } = FieldRequirement.Optional;

    /// <summary>Whether the family name must be supplied.</summary>
    public FieldRequirement Lastname { get; init; } = FieldRequirement.Optional;

    /// <summary>Shared defaults used when a realm has never configured the
    /// section. All three fields <see cref="FieldRequirement.Optional"/> —
    /// today's lenient behaviour.</summary>
    public static RegistrationFieldsSettings Defaults { get; } = new();
}
