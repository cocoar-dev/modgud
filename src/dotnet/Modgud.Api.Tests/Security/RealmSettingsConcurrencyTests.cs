using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// F7-05 — realm settings (including the sign-in policy) are one document written as a whole.
/// A stale tab must not silently overwrite what another admin saved since: the GET carries the
/// document's version, a PATCH with an outdated <c>ExpectedVersion</c> is refused with 409.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmSettingsConcurrencyTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task A_patch_based_on_an_outdated_version_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await PatchAsync(new { Branding = new { ProductName = "Seed" } }, ct);
        var seen = await VersionAsync(ct);

        // Another admin saves meanwhile.
        var other = await PatchAsync(new { Branding = new { ProductName = "Changed by someone else" }, ExpectedVersion = seen }, ct);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // The stale tab saves on top of the version it loaded.
        var stale = await PatchAsync(new { Branding = new { ProductName = "Stale tab" }, ExpectedVersion = seen }, ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("RealmSettings.Changed", await stale.Content.ReadAsStringAsync(ct));
        using var doc = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/realm-settings", ct));
        Assert.Equal("Changed by someone else", doc.RootElement.GetProperty("Branding").GetProperty("ProductName").GetString());
    }

    private Task<HttpResponseMessage> PatchAsync(object body, CancellationToken ct) =>
        Client.PatchAsJsonAsync("/api/admin/realm-settings", body, ct);

    private async Task<string> VersionAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/realm-settings", ct));
        return doc.RootElement.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
    }
}
