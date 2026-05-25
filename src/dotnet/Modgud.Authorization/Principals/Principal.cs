using System.Text.Json.Serialization;

namespace Modgud.Authorization.Principals;

// JsonIgnore is used on DisplayName below to keep the computed name out of the
// persisted document — it's derived from other fields each access.

/// <summary>
/// Base class for every principal persisted by the library. Stored polymorphically
/// via Marten (one document table, type discriminator column) — concrete classes
/// register their alias at startup with <c>RegisterPrincipalType&lt;T&gt;("alias")</c>.
/// <para>
/// Apps extend by deriving from the shipped concrete types (<see cref="Group"/>,
/// <see cref="ServiceAccount"/>, or a custom <c>Person</c>-derived class) and adding
/// their own fields. The library-side services only ever reach into the capability
/// interfaces (<see cref="IPrincipalWithMembers"/> etc.), so app-specific fields
/// pass through without library changes.
/// </para>
/// </summary>
public abstract class Principal : IPrincipal
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }

    /// <summary>
    /// How this principal presents itself in UI, logs, and notifications. Abstract
    /// because there's no single right source for it across principal types — a
    /// <see cref="Group"/> derives it from its name, a person from firstname+lastname,
    /// a service account from its account handle. Computed, never stored.
    /// </summary>
    [JsonIgnore]
    public abstract string DisplayName { get; }

    /// <summary>
    /// Discriminator string — concrete subclasses override to return a stable alias
    /// (<c>"person"</c>, <c>"group"</c>, <c>"service-account"</c>). Persisted as a
    /// JSON property so Marten LINQ filters (<c>p.Type == "person"</c>) translate
    /// to a JSONB-path query, and JsEval-emitted membership-script predicates work
    /// without needing a separate discriminator-mapping registration. Getter-only
    /// — STJ serializes the computed value, ignores it on deserialize (each access
    /// re-evaluates the override).
    /// </summary>
    public abstract string Type { get; }
}
