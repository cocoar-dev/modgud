using ErrorOr;
using Marten;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// The single canonical write path for creating <see cref="App"/> records, shared by
/// <see cref="AppsEndpoints"/> and the realm-provisioning applier so the manual path
/// and the manifest path can never diverge. Returns <see cref="ErrorOr{T}"/> so the
/// endpoint maps it to HTTP while the applier consumes it directly. The injected
/// <see cref="IDocumentSession"/> is tenant-scoped, so a call lands in whatever realm
/// the ambient <c>TenantContext</c> selects.
/// </summary>
public sealed class AppAdminService(IDocumentSession session)
{
    public async Task<ErrorOr<App>> CreateAppAsync(CreateAppDto dto, CancellationToken ct = default)
    {
        if (!AppSlugRules.IsValidFormat(dto.Slug))
            return Error.Validation("App.InvalidSlug",
                "Slug must be 3-63 characters, start with a letter, end with a letter or digit, and contain only lowercase letters, digits, and hyphens.");

        if (AppSlugRules.IsReserved(dto.Slug))
            return Error.Validation("App.ReservedSlug", $"The slug '{dto.Slug}' is reserved.");

        if (string.IsNullOrWhiteSpace(dto.DisplayName))
            return Error.Validation("App.DisplayNameRequired", "DisplayName is required.");

        var duplicate = await session.Query<App>()
            .Where(a => a.Slug == dto.Slug && !a.IsDeleted)
            .AnyAsync(ct);
        if (duplicate)
            return Error.Conflict("App.DuplicateSlug", $"An app with slug '{dto.Slug}' already exists.");

        var permissions = NormalizePermissions(dto.Permissions, existingByKey: null);
        if (permissions.IsError) return permissions.Errors;

        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new AppCreatedEvent(
            Id: id,
            Slug: dto.Slug,
            DisplayName: dto.DisplayName,
            Description: dto.Description,
            Permissions: permissions.Value,
            IsSystem: false));
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<App>(id, ct))!;
    }

    /// <summary>
    /// Validates and normalises the permission catalog off a create / update payload:
    /// parses incoming ids (ShortGuid → Guid, minting a fresh one when absent), dedupes
    /// by (Resource, Action), enforces the segment grammar and rejects the reserved
    /// <c>realm:admin</c> bypass. Shared by create (here) and the AppsEndpoints update
    /// path so there is one normalisation rule.
    /// </summary>
    internal static ErrorOr<List<AppPermission>> NormalizePermissions(
        List<AppPermissionDto>? payload,
        IReadOnlyDictionary<Guid, AppPermission>? existingByKey)
    {
        var input = payload ?? [];
        var normalised = new List<AppPermission>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in input)
        {
            var resource = entry.Resource?.Trim() ?? string.Empty;
            var action = entry.Action?.Trim() ?? string.Empty;

            if (!AppPermissionRules.IsValidSegment(resource) ||
                !AppPermissionRules.IsValidSegment(action))
            {
                return Error.Validation("App.InvalidPermissionSegment",
                    $"Permission '{resource}:{action}' is invalid — both segments must match ^[a-z0-9-]+$.");
            }

            // realm:admin is the synthetic realm-wide bypass — it must never be a catalog
            // entry (audit H1, vector 3). Conferring realm:admin is reserved to a role's
            // IsRealmAdmin flag, which is itself gated on the caller holding realm:admin.
            if (AppPermissionRules.IsReservedBypass(resource, action))
            {
                return Error.Validation("App.ReservedPermission",
                    "The permission 'realm:admin' is reserved — it is the realm-wide bypass and cannot be a catalog entry. Use a role's IsRealmAdmin flag instead.");
            }

            var key = $"{resource}:{action}";
            if (!seen.Add(key))
                continue; // silently drop exact duplicates

            // Explicit id wins (rename / detached-replay path); otherwise mint a new one.
            var id = Guid.NewGuid();
            if (!string.IsNullOrEmpty(entry.Id) && BuildingBlocks.Helper.ShortGuid.TryParse(entry.Id, out Guid parsed))
                id = parsed;

            var description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description.Trim();
            normalised.Add(new AppPermission(id, resource, action, description));
        }

        return normalised;
    }
}
