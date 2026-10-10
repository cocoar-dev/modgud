using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Features.Dashboard;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Audit;
using Modgud.Domain.Dashboard;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Audit;

namespace Modgud.Api.Tests.Dashboard;

[Collection(IntegrationTestCollection.Name)]
public class DashboardEndpointsTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ── Statistics ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_bucket_logins_per_day_and_fold_failure_streaks_into_the_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var day = DateTimeOffset.UtcNow.Date.AddDays(-2);
        var noon = new DateTimeOffset(day.AddHours(12), TimeSpan.Zero);

        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(
                Login(noon, "password"),
                Login(noon.AddMinutes(1), "password"),
                Login(noon.AddMinutes(2), "password"),
                Login(noon.AddMinutes(3), "magic_link"),
                Audit(noon.AddMinutes(4), AuditEvents.LoginFailuresObserved, count: 4),
                Audit(noon.AddMinutes(5), AuditEvents.LoginFailed),
                // Not a login at all — must not leak into either series.
                Audit(noon.AddMinutes(6), AuditEvents.AccountLockedOut),
                // Outside the requested window.
                Login(noon.AddDays(-60), "passkey"));
            await session.SaveChangesAsync(ct);
        }

        var stats = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats?days=30&tz=UTC", ct);

        Assert.NotNull(stats?.Logins);
        Assert.Equal(30, stats!.Logins!.Series.Count);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), stats.Logins.Series[^1].Day);

        var bucket = Assert.Single(stats.Logins.Series, s => s.Day == day.ToString("yyyy-MM-dd"));
        Assert.Equal(4, bucket.Succeeded);
        Assert.Equal(5, bucket.Failed);

        Assert.Equal(1, Assert.Single(stats.Logins.Methods, m => m.Method == "magic_link").Count);
        Assert.DoesNotContain(stats.Logins.Methods, m => m.Method == "passkey");
        Assert.Equal(5, stats.Logins.Failed);
    }

    [Fact]
    public async Task Stats_bucket_by_the_viewers_calendar_day_not_utc()
    {
        var ct = TestContext.Current.CancellationToken;
        // 23:30 UTC is already the next calendar day in Vienna (UTC+1/+2).
        var utcDay = DateTimeOffset.UtcNow.Date.AddDays(-5);
        var lateEvening = new DateTimeOffset(utcDay.AddHours(23).AddMinutes(30), TimeSpan.Zero);

        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(Login(lateEvening, "password"));
            await session.SaveChangesAsync(ct);
        }

        var utc = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats?tz=UTC", ct);
        var vienna = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats?tz=Europe/Vienna", ct);
        var bogus = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats?tz=Not/A_Zone", ct);

        Assert.Equal(1, utc!.Logins!.Series.Single(s => s.Day == utcDay.ToString("yyyy-MM-dd")).Succeeded);

        Assert.Equal("Europe/Vienna", vienna!.TimeZone);
        Assert.Equal(0, vienna.Logins!.Series.Single(s => s.Day == utcDay.ToString("yyyy-MM-dd")).Succeeded);
        Assert.Equal(1, vienna.Logins.Series.Single(s => s.Day == utcDay.AddDays(1).ToString("yyyy-MM-dd")).Succeeded);

        // An unknown zone buckets in UTC rather than failing the dashboard.
        Assert.Equal("UTC", bogus!.TimeZone);
    }

    [Fact]
    public async Task Stats_split_security_events_by_severity_and_rank_the_ones_needing_attention()
    {
        var ct = TestContext.Current.CancellationToken;
        var day = DateTimeOffset.UtcNow.Date.AddDays(-3);
        var noon = new DateTimeOffset(day.AddHours(12), TimeSpan.Zero);

        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(
                Security(noon, AuditEvents.LoginFailedUnknownUser, AuditSeverity.Warning),
                Security(noon.AddMinutes(1), AuditEvents.LoginFailedUnknownUser, AuditSeverity.Warning),
                Security(noon.AddMinutes(2), AuditEvents.RefreshTokenReuseDetected, AuditSeverity.Error),
                Security(noon.AddMinutes(3), AuditEvents.SigningKeyRotated, AuditSeverity.Info));
            await session.SaveChangesAsync(ct);
        }

        var stats = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats?tz=UTC", ct);

        var bucket = Assert.Single(stats!.Security!.Series, s => s.Day == day.ToString("yyyy-MM-dd"));
        Assert.Equal(1, bucket.Info);
        Assert.Equal(2, bucket.Warning);
        Assert.Equal(1, bucket.Error);

        Assert.Equal(2, Assert.Single(
            stats.Security.TopEventTypes, t => t.EventType == AuditEvents.LoginFailedUnknownUser).Count);
        // Routine Info events are not "needs attention".
        Assert.DoesNotContain(stats.Security.TopEventTypes, t => t.EventType == AuditEvents.SigningKeyRotated);
    }

    [Fact]
    public async Task Stats_count_what_the_list_surfaces_show()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync("Plain", "Person", "PP", "pp@test.com");
        await Factory.WaitForProjectionsAsync();

        var stats = await Client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats", ct);
        var users = await Client.GetFromJsonAsync<List<object>>("/api/user", ct);
        var roles = await Client.GetFromJsonAsync<List<object>>("/api/role", ct);

        Assert.Equal(users!.Count, stats!.Counts.Users);
        Assert.Equal(roles!.Count, stats.Counts.Roles);
        Assert.True(stats.Counts.ActiveSessions >= 1, "the calling admin's own session is active");
        Assert.Equal(0, stats.Counts.PendingChangeRequests);
    }

    [Fact]
    public async Task Stats_withhold_every_block_the_caller_has_no_permission_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync("Plain", "Person", "PP", "pp@test.com");
        var plain = await CreateAuthenticatedClientAsync("pp", DefaultPassword);

        var stats = await plain.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard/stats", ct);

        Assert.NotNull(stats);
        Assert.Equal(new DashboardCountsDto(null, null, null, null, null, null, null, null, null, null, null, null, null), stats!.Counts);
        Assert.Null(stats.Logins);
        Assert.Null(stats.Security);
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Layout_is_per_user_and_resets_to_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync("Plain", "Person", "PP", "pp@test.com");
        var plain = await CreateAuthenticatedClientAsync("pp", DefaultPassword);

        var initial = await plain.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct);
        Assert.Null(initial!.User);
        Assert.Null(initial.RealmDefault);

        var saved = await plain.PutAsJsonAsync("/api/dashboard/layout", Layout(("my-sessions", "m"), ("logins-chart", "xl")), ct);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var mine = await plain.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct);
        Assert.Equal(
            [new DashboardWidgetPlacement("my-sessions", "m"), new DashboardWidgetPlacement("logins-chart", "xl")],
            mine!.User);

        // Another user's dashboard is untouched by it.
        var theirs = await Client.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct);
        Assert.Null(theirs!.User);

        Assert.Equal(HttpStatusCode.NoContent, (await plain.DeleteAsync("/api/dashboard/layout", ct)).StatusCode);
        Assert.Null((await plain.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct))!.User);
    }

    [Fact]
    public async Task Realm_default_needs_realm_settings_write_and_reaches_every_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await Factory.CreateTestUserWithIdentityAsync("Plain", "Person", "PP", "pp@test.com");
        var plain = await CreateAuthenticatedClientAsync("pp", DefaultPassword);
        var layout = Layout(("account-security", "l"));

        var refused = await plain.PutAsJsonAsync("/api/admin/dashboard/default-layout", layout, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await plain.DeleteAsync("/api/admin/dashboard/default-layout", ct)).StatusCode);

        var accepted = await Client.PutAsJsonAsync("/api/admin/dashboard/default-layout", layout, ct);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        var seen = await plain.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct);
        Assert.Null(seen!.User);
        Assert.Equal([new DashboardWidgetPlacement("account-security", "l")], seen.RealmDefault);

        Assert.Equal(HttpStatusCode.NoContent,
            (await Client.DeleteAsync("/api/admin/dashboard/default-layout", ct)).StatusCode);
        Assert.Null((await plain.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct))!.RealmDefault);
    }

    [Fact]
    public async Task Layout_keeps_a_widgets_options()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new SaveDashboardLayoutDto(
        [
            new DashboardWidgetPlacement("directory", "l", new() { ["show"] = ["Users", "Positions"] }),
            new DashboardWidgetPlacement("my-sessions", "m"),
        ]);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PutAsJsonAsync("/api/dashboard/layout", body, ct)).StatusCode);

        var stored = (await Client.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct))!.User!;
        Assert.Equal(["Users", "Positions"], stored[0].Options!["show"]);
        Assert.Null(stored[1].Options);
    }

    [Theory]
    [InlineData("show me", "Users")]
    [InlineData("show", "<script>")]
    public async Task Layout_rejects_malformed_options(string key, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new SaveDashboardLayoutDto(
            [new DashboardWidgetPlacement("directory", "l", new() { [key] = [value] })]);

        var response = await Client.PutAsJsonAsync("/api/dashboard/layout", body, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task My_apps_lists_only_the_callers_own_valid_grants_one_row_per_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await Factory.CreateTestUserWithIdentityAsync("Plain", "Person", "PP", "pp@test.com");
        var app = new Modgud.Authorization.Apps.App { Id = Guid.NewGuid(), Slug = "notes", DisplayName = "Notes" };
        var client = new OAuthApplicationState
        {
            Id = Guid.NewGuid(), ClientId = "notes-web", DisplayName = "Notes Web", AppIds = [app.Id],
        };
        var bare = new OAuthApplicationState { Id = Guid.NewGuid(), ClientId = "cli-tool" };
        var now = DateTimeOffset.UtcNow;

        await using (var session = GetTenantedDocumentSession())
        {
            session.Store(app);
            session.Store(client, bare);
            session.Store(
                Grant(DefaultUser!.Id, client.Id, now.AddDays(-9)),
                Grant(DefaultUser.Id, client.Id, now.AddDays(-1)),
                Grant(DefaultUser.Id, bare.Id, now.AddDays(-3)),
                Grant(DefaultUser.Id, bare.Id, now.AddDays(-2), status: "revoked"),
                Grant(other.Id, client.Id, now));
            await session.SaveChangesAsync(ct);
        }

        var mine = await Client.GetFromJsonAsync<List<MyAppDto>>("/api/dashboard/my-apps", ct);

        Assert.Equal(["Notes Web", "cli-tool"], mine!.Select(m => m.Name));
        Assert.Equal("Notes", mine[0].AppName);
        Assert.Null(mine[1].AppName);
        Assert.True(mine[0].FirstAuthorizedAt < mine[0].LastAuthorizedAt);
    }

    [Theory]
    [InlineData("logins-chart", "huge")]
    [InlineData("Not A Widget", "m")]
    [InlineData("", "m")]
    public async Task Layout_rejects_a_malformed_placement(string widget, string size)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await Client.PutAsJsonAsync("/api/dashboard/layout", Layout((widget, size)), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Dashboard.InvalidLayout", await response.Content.ReadAsStringAsync(ct));
        Assert.Null((await Client.GetFromJsonAsync<DashboardLayoutsDto>("/api/dashboard/layout", ct))!.User);
    }

    [Fact]
    public async Task Layout_rejects_the_same_widget_twice()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await Client.PutAsJsonAsync(
            "/api/dashboard/layout", Layout(("logins-chart", "m"), ("logins-chart", "xl")), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SaveDashboardLayoutDto Layout(params (string Widget, string Size)[] widgets) =>
        new(widgets.Select(w => new DashboardWidgetPlacement(w.Widget, w.Size)).ToList());

    private static OpenIddictAuthorizationDocument Grant(
        Guid user, Guid client, DateTimeOffset at, string status = "valid") => new()
    {
        ApplicationId = client.ToString(),
        Subject = user.ToString(),
        Status = status,
        Type = "permanent",
        CreationDate = at,
    };

    private static AuthAuditView Login(DateTimeOffset at, string method) =>
        Audit(at, AuditEvents.LoginSucceeded) with { Method = method };

    private static AuthAuditView Audit(DateTimeOffset at, string eventType, int? count = null) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = at,
        Category = AuditCategories.Authentication,
        EventType = eventType,
        UserId = Guid.NewGuid(),
        Count = count,
        Level = eventType == AuditEvents.LoginSucceeded ? "Info" : "Warning",
    };

    private static RealmSecurityAuditEvent Security(DateTimeOffset at, string eventType, AuditSeverity severity) => new()
    {
        Timestamp = at,
        EventType = eventType,
        Category = AuditEvents.CategoryOf(eventType),
        Severity = severity,
        OutcomeCode = AuditOutcomes.Observed,
    };
}
