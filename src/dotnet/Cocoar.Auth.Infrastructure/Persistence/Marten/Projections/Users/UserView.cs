using Marten.Schema;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;

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
    /// IdP-config ids the user has an active external-identity link with.
    /// Empty = local-only user. Multi-entry = linked to several IdPs.
    /// Drives the admin user list's "IdP-connected" indicator; frontend
    /// resolves ids to display names via the IdpConfig store.
    /// </summary>
    public List<Guid> ExternalIdpConfigIds { get; init; } = [];

    public string GetDisplayLabel()
    {
        var parts = new[] { Acronym, $"{Firstname ?? ""} {Lastname ?? ""}".Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var label = string.Join(" | ", parts);
        return !string.IsNullOrWhiteSpace(label) ? label : UserName ?? "";
    }
}
