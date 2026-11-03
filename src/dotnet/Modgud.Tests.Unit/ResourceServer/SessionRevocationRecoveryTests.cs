using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

public sealed class SessionRevocationRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = SessionRevocationRecoveryTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Responses.Dequeue()(request));
        public void Reply(string path, string json, HttpStatusCode status = HttpStatusCode.OK) => Responses.Enqueue(request =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            return new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
    }

    private const string Bootstrap = "/api/app/app/change-feed/session-revocations/snapshot";
    private const string Poll = "/api/app/app/change-feed";
    private const string EmptyPoll = """{"Messages":[],"HasMore":false}""";
    private const string EmptyHistory = """{"ContractVersion":1,"HistorySeconds":3900,"Cursor":"anchor","Revocations":[]}""";

    private static ModgudSessionRevocationWorker Worker(Transport transport, Clock clock, ModgudSessionDenylist denylist)
    {
        var options = new ModgudSessionRevocationOptions { AppId = "app", ClientId = "reader", ClientSecret = "test-only" };
        var client = new ModgudSessionFeedClient(transport,
            new ModgudResourceServerOptions { Authority = "https://id.example" }, options, clock,
            NullLogger<ModgudSessionFeedClient>.Instance);
        return new(client, denylist, options, clock, NullLogger<ModgudSessionRevocationWorker>.Instance);
    }

    [Fact]
    public async Task Recovery_uses_absolute_expiry_and_merges_without_shortening_known_entries()
    {
        using var transport = new Transport();
        transport.Reply("/connect/token", """{"access_token":"test-only","expires_in":3600}""");
        transport.Reply(Bootstrap, """
            {"ContractVersion":1,"HistorySeconds":3900,"Cursor":"anchor","Revocations":[
              {"SessionId":"old","EndedAt":"2026-10-19T10:00:00Z"},
              {"SessionId":"recent","EndedAt":"2026-10-19T11:00:00Z"},
              {"SessionId":"recent","EndedAt":"2026-10-19T11:01:00Z"},
              {"SessionId":"known","EndedAt":"2026-10-19T11:00:00Z"}]}
            """);
        transport.Reply(Poll, EmptyPoll);
        var clock = new Clock();
        var denylist = new ModgudSessionDenylist(clock);
        denylist.Revoke("known", Now.AddMinutes(30));
        await Worker(transport, clock, denylist).StepAsync(default);
        Assert.False(denylist.IsRevoked("old"));
        Assert.False(denylist.IsRevoked("active-unknown"));
        Assert.True(denylist.IsRevoked("recent"));
        clock.Now = Now.AddMinutes(6);
        Assert.False(denylist.IsRevoked("recent")); // not renewed to arrival + 65 min
        Assert.True(denylist.IsRevoked("known"));
        Assert.Empty(transport.Responses);
    }

    [Theory]
    [InlineData("404")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("window")]
    [InlineData("malformed")]
    [InlineData("overflow")]
    public async Task Incomplete_bootstrap_never_commits_cursor_or_sync_and_retries_recovery(string failure)
    {
        using var transport = new Transport();
        transport.Reply("/connect/token", """{"access_token":"test-only","expires_in":3600}""");
        var json = failure switch
        {
            "missing" => """{"Cursor":"unsafe"}""",
            "version" => EmptyHistory.Replace("\"ContractVersion\":1", "\"ContractVersion\":2"),
            "window" => EmptyHistory.Replace("3900", "1"),
            "malformed" => "{",
            "overflow" => """{"ContractVersion":1,"HistorySeconds":3900,"Cursor":"unsafe","Revocations":[{"SessionId":"unapplied","EndedAt":"2026-10-19T11:59:00Z"},{"SessionId":"overflow","EndedAt":"9999-12-31T23:59:59Z"}]}""",
            _ => "{}",
        };
        transport.Reply(Bootstrap, json, failure == "404" ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        transport.Reply(Bootstrap, EmptyHistory);
        transport.Reply(Poll, EmptyPoll);
        var clock = new Clock();
        var denylist = new ModgudSessionDenylist(clock);
        denylist.Revoke("known", Now.AddHours(1));
        var worker = Worker(transport, clock, denylist);
        await Assert.ThrowsAnyAsync<Exception>(() => worker.StepAsync(default));
        Assert.Null(denylist.LastSyncedAt);
        Assert.True(denylist.IsRevoked("known"));
        Assert.False(denylist.IsRevoked("unapplied"));
        await worker.StepAsync(default);
        Assert.NotNull(denylist.LastSyncedAt); // explicit empty history is supported
        Assert.True(denylist.IsRevoked("known"));
        Assert.Empty(transport.Responses);
    }

    [Theory]
    [InlineData(long.MaxValue, 0)]
    [InlineData(1, long.MaxValue)]
    [InlineData(1, long.MinValue)]
    public void Extreme_windows_fail_validation_without_arithmetic_overflow(long lifetimeTicks, long skewTicks)
    {
        Assert.Throws<OptionsValidationException>(() => new ServiceCollection().AddModgudResourceServer(o =>
        {
            o.Authority = "https://id.example";
            o.Audience = "api";
            o.SessionRevocation = new()
            {
                Enabled = true, AppId = "app", ClientId = "reader", ClientSecret = "test-only",
                AccessTokenLifetime = TimeSpan.FromTicks(lifetimeTicks), ClockSkew = TimeSpan.FromTicks(skewTicks),
            };
        }));
    }

    [Theory]
    [InlineData("409")]
    [InlineData("in-band")]
    public async Task Both_reset_protocols_recover_before_resuming_without_erasing_known_entries(string reset)
    {
        using var transport = new Transport();
        transport.Reply("/connect/token", """{"access_token":"test-only","expires_in":3600}""");
        transport.Reply(Bootstrap, EmptyHistory);
        transport.Reply(Poll, reset == "409" ? "{}" : """{"Messages":[{"Kind":"ResetRequired","Cursor":"reset"}]}""",
            reset == "409" ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        transport.Reply(Bootstrap, EmptyHistory.Replace("[]", """[{"SessionId":"missed","EndedAt":"2026-10-19T11:59:00Z"}]"""));
        transport.Reply(Poll, EmptyPoll);
        var clock = new Clock();
        var denylist = new ModgudSessionDenylist(clock);
        denylist.Revoke("known", Now.AddHours(1));
        var worker = Worker(transport, clock, denylist);
        Assert.Equal(TimeSpan.Zero, await worker.StepAsync(default));
        await worker.StepAsync(default);
        Assert.True(denylist.IsRevoked("missed"));
        Assert.True(denylist.IsRevoked("known"));
        Assert.Empty(transport.Responses);
    }

    [Fact]
    public async Task Token_and_followup_failures_preserve_known_entries_and_committed_anchor()
    {
        using var transport = new Transport();
        transport.Reply("/connect/token", "{}", HttpStatusCode.ServiceUnavailable);
        transport.Reply("/connect/token", """{"access_token":"test-only","expires_in":3600}""");
        transport.Reply(Bootstrap, EmptyHistory);
        transport.Reply(Poll, "{malformed");
        transport.Responses.Enqueue(request =>
        {
            Assert.Equal(Poll, request.RequestUri!.AbsolutePath);
            Assert.Contains("cursor=anchor", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(EmptyPoll) };
        });
        var clock = new Clock();
        var denylist = new ModgudSessionDenylist(clock);
        denylist.Revoke("known", Now.AddHours(1));
        var worker = Worker(transport, clock, denylist);
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.StepAsync(default));
        Assert.Null(denylist.LastSyncedAt);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => worker.StepAsync(default));
        Assert.Null(denylist.LastSyncedAt);
        Assert.True(denylist.IsRevoked("known"));
        await worker.StepAsync(default);
        Assert.NotNull(denylist.LastSyncedAt);
        Assert.Empty(transport.Responses);
    }

    [Theory]
    [InlineData(604500, 300, true)]
    [InlineData(604501, 300, false)]
    [InlineData(3600, -1, false)]
    [InlineData(0, 0, false)]
    public void Recovery_window_is_positive_nonnegative_skew_and_at_most_seven_days(int lifetime, int skew, bool valid)
    {
        void Register() => new ServiceCollection().AddModgudResourceServer(o =>
        {
            o.Authority = "https://id.example";
            o.Audience = "api";
            o.SessionRevocation = new()
            {
                Enabled = true, AppId = "app", ClientId = "reader", ClientSecret = "test-only",
                AccessTokenLifetime = TimeSpan.FromSeconds(lifetime), ClockSkew = TimeSpan.FromSeconds(skew),
            };
        });
        if (valid) Register();
        else Assert.Throws<OptionsValidationException>(Register);
    }
}
