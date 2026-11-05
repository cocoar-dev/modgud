using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.Security;

/// <summary>B3-EI-07 — the inbox settings PUT stored a missing section or an unbounded day
/// value, which broke the next retention run. Such settings are refused.</summary>
public sealed class InboxSettingsValidationTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Settings_the_retention_job_cannot_run_with_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingSection = JsonNode.Parse("""
            { "AdminChangeRequest": null,
              "ChangeRequestFeedback": { "MaxUnreadDays": 60, "AutoExpireDaysAfterRead": 30 },
              "ScheduledJobFeedback": { "MaxUnreadDays": 30, "AutoExpireDaysAfterRead": 14 } }
            """)!;
        using (var refused = await Client.PutAsJsonAsync("/api/admin/inbox-settings", missingSection, ct))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var hugeDays = JsonNode.Parse("""
            { "AdminChangeRequest": { "HardDeleteDaysAfterDismissed": 2147483647 },
              "ChangeRequestFeedback": { "MaxUnreadDays": 60, "AutoExpireDaysAfterRead": 30 },
              "ScheduledJobFeedback": { "MaxUnreadDays": 30, "AutoExpireDaysAfterRead": 14 } }
            """)!;
        using (var refused = await Client.PutAsJsonAsync("/api/admin/inbox-settings", hugeDays, ct))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
