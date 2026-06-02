using Marten.Schema;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.Users;

[DocumentAlias("user_view")]
public record UserView
{
    public Guid Id { get; init; }
    public string? UserName { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? Acronym { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsDeleted { get; init; }
    public bool HasPassword { get; init; }

    /// <summary>
    /// LoginProvider ids the user has an active external-identity link with.
    /// Empty = local-only user. Multi-entry = linked to several providers.
    /// Drives the admin user list's "IdP-connected" indicator; frontend
    /// resolves ids to display names via the LoginProvider store.
    /// <para>
    /// KNOWN LIMITATION (accepted): this is a deduplicated provider-id set, not
    /// per-link, so it cannot distinguish one link from two links to the SAME
    /// provider. A user holding two links from one provider who unlinks one will
    /// drop that provider from the indicator until their next link event or a
    /// projection rebuild. Cosmetic only — this field is never consulted for any
    /// authorization decision (durable membership reads `Person.ExternalIdentities`,
    /// which IS keyed per-link). A per-link fix would need a rebuild migration to
    /// reconstruct the history, which is not worth it for an admin-list badge.
    /// </para>
    /// </summary>
    public List<Guid> ExternalLoginProviderIds { get; init; } = [];

    public string GetDisplayLabel()
    {
        var parts = new[] { Acronym, $"{Firstname ?? ""} {Lastname ?? ""}".Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var label = string.Join(" | ", parts);
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        // UserName is the fallback only when it's actually present. A
        // whitespace-only username would otherwise render as a blank row in
        // admin grids — fall through to the explicit placeholder so something
        // is always visible.
        return string.IsNullOrWhiteSpace(UserName) ? "<no name>" : UserName;
    }
}
