using System.Net;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Pins the real pipeline boundary between realm-independent paths and
/// tenant-scoped session/DataProtection. These probes previously skipped realm
/// resolution but missed the terminal branch, causing an exception-backed 500.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RealmIndependentPathTests : IntegrationTestBase
{
    public RealmIndependentPathTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/swagger.json")]
    [InlineData("/swagger-ui.html")]
    [InlineData("/healthz")]
    [InlineData("/assets.backup")]
    public async Task Unknown_realm_independent_get_paths_return_404(string path)
    {
        using var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_realm_independent_post_path_returns_404()
    {
        using var response = await Client.PostAsync(
            "/install.php",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Real_realm_independent_endpoints_remain_reachable(string path)
    {
        using var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
