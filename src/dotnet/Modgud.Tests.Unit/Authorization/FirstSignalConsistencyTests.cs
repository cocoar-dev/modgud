using Modgud.Api.Features.Auth.OAuth;

namespace Modgud.Tests.Unit.Authorization;

/// <summary>
/// ADR-0011 Phase 2 — pins the first-signal-consistency decision matrix: a
/// request that entered on an Application subdomain (Host pinned an App) may only
/// proceed with a client that belongs to that App or is realm-wide. A client
/// bound to a different App is the cross-app confused-deputy surface and must be
/// rejected. The async wiring (authorize + token) drives this pure predicate.
/// </summary>
public class FirstSignalConsistencyTests
{
    private static readonly Guid AppX = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AppY = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void No_host_pin_is_never_a_violation()
    {
        Assert.False(AuthorizationEndpointHelpers.IsCrossAppViolation(null, [AppY]));
    }

    [Fact]
    public void Realm_wide_client_passes_under_any_host()
    {
        Assert.False(AuthorizationEndpointHelpers.IsCrossAppViolation(AppX, []));
    }

    [Fact]
    public void Client_bound_to_the_pinned_app_is_consistent()
    {
        Assert.False(AuthorizationEndpointHelpers.IsCrossAppViolation(AppX, [AppX]));
    }

    [Fact]
    public void Client_bound_to_the_pinned_app_among_others_is_consistent()
    {
        Assert.False(AuthorizationEndpointHelpers.IsCrossAppViolation(AppX, [AppY, AppX]));
    }

    [Fact]
    public void Client_bound_only_to_a_different_app_is_a_violation()
    {
        Assert.True(AuthorizationEndpointHelpers.IsCrossAppViolation(AppX, [AppY]));
    }
}
