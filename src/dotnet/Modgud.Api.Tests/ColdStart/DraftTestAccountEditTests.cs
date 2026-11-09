using System.Net.Http.Json;
using System.Text.Json;
using Modgud.Api.Tests.Infrastructure;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Reported 2026-11-08: a test account created through the draft and applied could not get a
/// plain first-name change — the next draft's plan reported a conflict for that user. These
/// tests stage exactly what the user editor stages (the whole entity), so the plan sees the
/// same input the admin console sends.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DraftTestAccountEditTests : IntegrationTestBase
{
    public DraftTestAccountEditTests(SharedPostgresFixture fixture) : base(fixture) { }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_test_account_created_through_the_draft_takes_a_first_name_change_without_a_conflict()
    {
        // 1. Create the test account in a draft, as the editor stages a new user, and apply.
        var created = await StageAsync(new
        {
            Email = "draft-ta@test.com", EmailConfirmed = true, IsActive = true,
            Firstname = "Draft", Lastname = "Tester", Acronym = (string?)null, UserName = "draft-ta",
            Password = "TestPass1234!", GracePeriodDaysOverride = (int?)null, TwoFactorExempt = false,
            IsTestAccount = true, FixedEmailCode = "246810", Id = "#draft-ta",
        });
        await ApplyAsync(created);

        // 2. Edit only the first name, as the editor stages a live user (its real id).
        var live = await LiveUserAsync("draft-ta");
        var edited = await StageAsync(new
        {
            Email = "draft-ta@test.com", EmailConfirmed = live.GetProperty("EmailConfirmed").GetBoolean(), IsActive = true,
            Firstname = "Renamed", Lastname = "Tester", Acronym = (string?)null, UserName = "draft-ta",
            GracePeriodDaysOverride = (int?)null, TwoFactorExempt = false,
            IsTestAccount = true, Id = live.GetProperty("Id").GetString(),
        });

        var plan = await PlanAsync(edited);
        var conflicts = plan.RootElement.GetProperty("Sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("Entries").EnumerateArray())
            .Where(e => e.GetProperty("Conflicts").GetArrayLength() > 0)
            .Select(e => e.ToString())
            .ToList();
        Assert.True(conflicts.Count == 0, "plan reports conflicts:\n" + string.Join("\n", conflicts));

        var user = plan.RootElement.GetProperty("Sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("Entries").EnumerateArray())
            .Single(e => e.GetProperty("Key").GetString() == "draft-ta");
        Assert.Equal("update", user.GetProperty("Action").GetString());
        var changed = user.GetProperty("Changes").EnumerateArray().Select(c => c.GetProperty("Field").GetString()).ToList();
        Assert.Equal(["Firstname"], changed);
    }

    private async Task<string> StageAsync(object entity)
    {
        var stage = await Client.PutAsJsonAsync("/api/admin/realm-config/drafts/active/entities/users", entity, Ct);
        var body = await stage.Content.ReadAsStringAsync(Ct);
        Assert.True(stage.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("Id").GetString()!;
    }

    private async Task<JsonDocument> PlanAsync(string draftId)
    {
        var plan = await Client.PostAsJsonAsync($"/api/admin/realm-config/drafts/{draftId}/plan", new { }, Ct);
        var body = await plan.Content.ReadAsStringAsync(Ct);
        Assert.True(plan.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body);
    }

    private async Task ApplyAsync(string draftId)
    {
        var apply = await Client.PostAsJsonAsync($"/api/admin/realm-config/drafts/{draftId}/apply", new { }, Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> LiveUserAsync(string userName)
    {
        using var export = JsonDocument.Parse(await Client.GetStringAsync("/api/admin/realm-config/export", Ct));
        return export.RootElement.GetProperty("Users").EnumerateArray()
            .Single(u => u.TryGetProperty("UserName", out var n) && n.GetString() == userName)
            .Clone();
    }
}
