using BuildingBlocks.Helper;
using Modgud.Api.Features.Admin.Apps;

namespace Modgud.Tests.Unit.Api.Features.Admin;

/// <summary>
/// F7-03 / B7-SC03 — the catalog normalization deduped by Resource:Action only and took
/// incoming ids as they came. Two different entries with the same id were accepted; the next
/// update (and permission resolution) builds a dictionary by id and breaks on the collision.
/// </summary>
public sealed class AppPermissionCatalogTests
{
    [Fact]
    public void Two_entries_with_the_same_id_are_refused()
    {
        var id = ShortGuid.Encode(Guid.NewGuid());
        var result = AppAdminService.NormalizePermissions(
            [new AppPermissionDto(id, "policy", "read", null), new AppPermissionDto(id, "policy", "write", null)],
            existingByKey: null);

        Assert.True(result.IsError, "a duplicate permission id must be refused");
        Assert.Equal("App.DuplicatePermissionId", result.FirstError.Code);
    }

    [Fact]
    public void Distinct_ids_and_minted_ids_are_accepted()
    {
        var result = AppAdminService.NormalizePermissions(
            [new AppPermissionDto(ShortGuid.Encode(Guid.NewGuid()), "policy", "read", null),
             new AppPermissionDto(null, "policy", "write", null)],
            existingByKey: null);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Select(p => p.Id).Distinct().Count());
    }
}
