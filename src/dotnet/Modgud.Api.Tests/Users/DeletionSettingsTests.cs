using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// WS6 of the Account-Lifecycle plan: per-realm deletion policy on
/// <c>RealmSettings</c>. Verifies the new <c>Deletion</c> sub-section reads
/// its defaults, persists a valid patch, and rejects an incoherent one
/// (reminder lead must be shorter than the grace window).
/// </summary>
public class DeletionSettingsTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Endpoint = "/api/admin/realm-settings";

    [Fact]
    public async Task Defaults_are_surfaced_when_never_configured()
    {
        var dto = await Client.GetFromJsonAsync<RealmSettingsDto>(
            Endpoint, JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(dto);
        Assert.Equal(30, dto!.Deletion.GraceDays);
        Assert.Equal(2, dto.Deletion.ReminderLeadDays);
        Assert.Equal(30, dto.Deletion.AdminRetentionDays);
        Assert.True(dto.Deletion.AutoPurgeEnabled);
    }

    [Fact]
    public async Task Valid_patch_persists()
    {
        var patch = new UpdateRealmSettingsDto
        {
            Deletion = new UpdateDeletionSettingsDto
            {
                GraceDays = 14,
                ReminderLeadDays = 3,
                AdminRetentionDays = 60,
                AutoPurgeEnabled = false,
            },
        };

        var resp = await Client.PatchAsJsonAsync(
            Endpoint, patch, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await Client.GetFromJsonAsync<RealmSettingsDto>(
            Endpoint, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(14, dto!.Deletion.GraceDays);
        Assert.Equal(3, dto.Deletion.ReminderLeadDays);
        Assert.Equal(60, dto.Deletion.AdminRetentionDays);
        Assert.False(dto.Deletion.AutoPurgeEnabled);
    }

    [Fact]
    public async Task Patch_rejected_when_reminder_lead_not_shorter_than_grace()
    {
        var patch = new UpdateRealmSettingsDto
        {
            Deletion = new UpdateDeletionSettingsDto
            {
                GraceDays = 5,
                ReminderLeadDays = 5, // must be < grace, else the reminder never fires
            },
        };

        var resp = await Client.PatchAsJsonAsync(
            Endpoint, patch, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
