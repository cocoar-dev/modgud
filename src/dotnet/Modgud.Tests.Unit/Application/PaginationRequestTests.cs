using Modgud.Application.DTOs.OAuth;

namespace Modgud.Tests.Unit.Application;

/// <summary>
/// Pinning tests for <see cref="PaginationRequest"/>'s <c>WithDefaults</c>
/// factory. The two list endpoints (<c>OAuthApisEndpoints</c>,
/// <c>OAuthClientsEndpoints</c>) bind <c>?page=</c>/<c>?pageSize=</c> as
/// <see cref="int"/> so absent query params arrive as 0 — the factory
/// must clamp them to the same defaults the parameterless constructor uses.
/// </summary>
public class PaginationRequestTests
{
    [Theory]
    [InlineData(0, 0, 1, 20)]    // both absent → defaults
    [InlineData(-5, -7, 1, 20)]  // both negative → defaults
    [InlineData(2, 50, 2, 50)]   // both valid → passed through
    [InlineData(0, 50, 1, 50)]   // page absent only
    [InlineData(2, 0, 2, 20)]    // pageSize absent only
    public void WithDefaults_clamps_non_positive_values_to_1_and_20(
        int rawPage, int rawPageSize, int expectedPage, int expectedPageSize)
    {
        var r = PaginationRequest.WithDefaults(rawPage, rawPageSize);

        Assert.Equal(expectedPage, r.Page);
        Assert.Equal(expectedPageSize, r.PageSize);
    }

    [Fact]
    public void Parameterless_constructor_defaults_match_WithDefaults_clamp_targets()
    {
        // Sanity-pin: the two paths to a "default" pagination must agree on
        // the same target values. If either side drifts, the other should
        // be updated to match.
        var implicitDefault = new PaginationRequest();
        var clamped = PaginationRequest.WithDefaults(0, 0);

        Assert.Equal(implicitDefault.Page, clamped.Page);
        Assert.Equal(implicitDefault.PageSize, clamped.PageSize);
    }
}
