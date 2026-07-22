namespace Modgud.Domain.Realms;

/// <summary>
/// A single named PageBuilder variant for a page slot (ADR-0001). The
/// <see cref="Schema"/> is opaque JSON — the SPA's <c>@cocoar/vue-page-builder</c>
/// renderer is the schema-shape authority; the backend only stores + serves it.
/// </summary>
public class PageVariant
{
    /// <summary>Stable id (a GUID string) assigned on create. Referenced by
    /// the slot's active-selection pointer and by any Application that
    /// activates a realm variant.</summary>
    public string Id { get; set; } = default!;

    /// <summary>Operator-facing name, e.g. "Split screen", "Minimal".</summary>
    public string Name { get; set; } = default!;

    /// <summary>Serialized <c>PageNode</c> tree as JSON.</summary>
    public string Schema { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Realm-level configuration for one page slot: a library of named variants
/// plus which one is active (ADR-0001). <see cref="ActiveVariantId"/> is
/// <c>null</c> ⇒ the slot renders the SPA's built-in hardcoded view
/// (i.e. "deactivated" — variants may still exist, unused).
/// </summary>
public class RealmPageSlot
{
    public List<PageVariant> Variants { get; set; } = new();

    /// <summary>Id of the active variant, or <c>null</c> for the built-in
    /// hardcoded view. An id that no longer matches a variant also resolves
    /// to built-in (defensive).</summary>
    public string? ActiveVariantId { get; set; }
}

/// <summary>
/// Application-level selection for one page slot (ADR-0001). An Application does
/// not author its own variants — the variant library is realm-global; the App
/// merely *selects* which realm variant is live for it. When
/// <see cref="InheritActive"/> is <c>true</c> (default) the effective page is
/// the realm's active selection; when <c>false</c> the App overrides it —
/// <see cref="ActiveVariantId"/> <c>null</c> ⇒ built-in, else a realm variant id.
/// </summary>
public class AppPageSlot
{
    /// <summary>When <c>true</c> the Application defers to the realm's active
    /// selection for this slot. When <c>false</c> the Application's own
    /// selection (<see cref="ActiveVariantId"/>) wins.</summary>
    public bool InheritActive { get; set; } = true;

    /// <summary>Only meaningful when <see cref="InheritActive"/> is
    /// <c>false</c>. <c>null</c> ⇒ built-in hardcoded view; else a *realm*
    /// variant id (Applications select from the realm library).</summary>
    public string? ActiveVariantId { get; set; }
}
