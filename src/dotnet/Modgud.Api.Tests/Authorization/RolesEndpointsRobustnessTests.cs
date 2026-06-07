using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Stage 6 of the cold-start ladder (no silent/cryptic failures). A role-create
/// payload that omits <c>PermissionIds</c> — a client on a stale/renamed DTO
/// shape, or just a partial body — used to dereference a null <c>List</c> in
/// <c>BuildRoleAsync</c> and return a 500 <c>NullReferenceException</c>. An
/// operator creating a role must instead get a clean, actionable 400.
///
/// <para>Discovered via the human-path E2E run: <c>10-admin §7</c> and all of
/// <c>20-permission-gating</c> were sending the pre-refactor role shape
/// (<c>AppSlug</c>/<c>ResourceType</c>/<c>Permissions</c>) and hitting the 500.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RolesEndpointsRobustnessTests : IntegrationTestBase
{
    public RolesEndpointsRobustnessTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Create_role_omitting_PermissionIds_returns_a_clean_400_not_500()
    {
        // No AppId, no PermissionIds (the field STJ binds to null), and not a
        // realm-admin role → this role grants nothing. The expected outcome is a
        // clear 400 naming the problem, never a 500 NullReferenceException.
        var res = await Client.PostAsJsonAsync("/api/role", new
        {
            Name = "Stale Shape Role",
            IsRealmAdmin = false,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Carries one of the actionable Role.* error codes, not a bare 500.
        Assert.Contains("Role.", body);
    }

    [Fact]
    public async Task Create_realm_admin_role_without_PermissionIds_succeeds()
    {
        // Positive control: a valid realm-admin role legitimately has no
        // PermissionIds. This exercises the same null-PermissionIds path and
        // must succeed — proving the null-coalesce fixes the crash without
        // breaking the happy path.
        var res = await Client.PostAsJsonAsync("/api/role", new
        {
            Name = "Stage6 Realm Admin Role",
            IsRealmAdmin = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Stage6 Realm Admin Role", body);
    }
}
