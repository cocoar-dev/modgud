using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Modgud.Api.Tests.Infrastructure;

// No collection/fixture: these tests use only in-memory HTTP pipelines, no DB.
public sealed class TestFailureCorrelationTests : IDisposable
{
    private const string Sentinel = "NEVER_EXPORT_DIAGNOSTIC_SENTINEL";
    private readonly string _directory = Directory.CreateTempSubdirectory("modgud-failure-").FullName;
    private TestFailureCorrelation? _correlation;

    [Fact]
    public async Task Handled_exception_is_correlated_to_500_without_exporting_values()
    {
        using var server = CreateHost(_ =>
        {
            var pg = new PostgresException(Sentinel, Sentinel, Sentinel, "23505",
                detail: Sentinel, hint: Sentinel, schemaName: Sentinel, tableName: Sentinel);
            throw new InvalidOperationException(Sentinel, pg) { HelpLink = Sentinel, Data = { ["value"] = Sentinel } };
        });
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Sentinel}?token={Sentinel}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Sentinel}");
        request.Headers.TryAddWithoutValidation("Cookie", $"session={Sentinel}");
        request.Headers.TryAddWithoutValidation(TestFailureCorrelation.ResponseHeader, Sentinel);
        request.Content = new StringContent(Sentinel);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("handled-body", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var report = ReadReport(response);
        var root = report.RootElement;
        Assert.Equal(["Exceptions", "Outcome", "RequestId", "StatusCode"],
            root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Http500", root.GetProperty("Outcome").GetString());
        Assert.Equal(500, root.GetProperty("StatusCode").GetInt32());
        var exceptions = root.GetProperty("Exceptions").EnumerateArray().ToArray();
        Assert.Equal(2, exceptions.Length);
        foreach (var exception in exceptions)
            Assert.Equal(["Methods", "SqlState", "Type"],
                exception.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(typeof(InvalidOperationException).FullName, exceptions[0].GetProperty("Type").GetString());
        Assert.NotEmpty(exceptions[0].GetProperty("Methods").EnumerateArray());
        Assert.Equal("23505", exceptions[1].GetProperty("SqlState").GetString());
        Assert.Empty(exceptions[1].GetProperty("Methods").EnumerateArray());
        Assert.False(root.GetRawText().Contains(Sentinel, StringComparison.Ordinal));
        Assert.Contains($"MODGUD_TEST_FAILURE {root.GetProperty("RequestId").GetString()} Http500",
            TestContext.Current.TestOutputHelper!.Output);
        Assert.Single(Directory.GetFiles(_directory));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Directory.GetFiles(_directory).Single()));
    }

    [Fact]
    public async Task Explicit_500_has_no_invented_exception_and_preserves_body_and_headers()
    {
        using var server = CreateHost(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.Headers["Set-Cookie"] = "synthetic=value";
            await context.Response.WriteAsync("explicit-body");
        });
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal("explicit-body", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("synthetic=value", Assert.Single(response.Headers.GetValues("Set-Cookie")));
        using var report = ReadReport(response);
        Assert.Empty(report.RootElement.GetProperty("Exceptions").EnumerateArray());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Non500_status_body_and_auth_headers_are_unchanged(int status)
    {
        using var server = CreateHost(async context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers["Location"] = "/login";
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            context.Response.Headers["Set-Cookie"] = "synthetic=value";
            await context.Response.WriteAsync("unchanged-body");
        });
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("unchanged-body", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
        Assert.Equal("Bearer", Assert.Single(response.Headers.GetValues("WWW-Authenticate")));
        Assert.Equal("synthetic=value", Assert.Single(response.Headers.GetValues("Set-Cookie")));
        Assert.False(response.Headers.Contains(TestFailureCorrelation.ResponseHeader));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Overlapping_requests_keep_distinct_exception_correlations()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var server = CreateHost(async context =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
            await bothEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (context.Request.Path == "/first") throw new ArgumentException(Sentinel);
            throw new InvalidOperationException(Sentinel);
        });
        using var client = CreateClient(server);
        var responses = await Task.WhenAll(
            client.GetAsync("/first", TestContext.Current.CancellationToken),
            client.GetAsync("/second", TestContext.Current.CancellationToken));
        using var first = responses[0];
        using var second = responses[1];
        using var report1 = ReadReport(first);
        using var report2 = ReadReport(second);
        Assert.NotEqual(report1.RootElement.GetProperty("RequestId").GetString(),
            report2.RootElement.GetProperty("RequestId").GetString());
        Assert.Equal(typeof(ArgumentException).FullName,
            report1.RootElement.GetProperty("Exceptions")[0].GetProperty("Type").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName,
            report2.RootElement.GetProperty("Exceptions")[0].GetProperty("Type").GetString());
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task Diagnostic_IO_failure_does_not_replace_the_response()
    {
        using var server = CreateHost(async context =>
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("original-response");
        });
        // Constructor already validated the directory; fail only diagnostic I/O.
        Directory.Delete(_directory);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("original-response", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = Assert.Single(response.Headers.GetValues(TestFailureCorrelation.ResponseHeader));
        Assert.Contains($"MODGUD_TEST_FAILURE_WRITE_FAILED {id}", TestContext.Current.TestOutputHelper!.Output);
    }

    [Fact]
    public async Task Escaping_exception_preserves_identity_and_is_not_labelled_as_HTTP500()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var original = new InvalidOperationException(Sentinel);
        new TestFailureCorrelation(_directory).Configure(builder =>
            builder.Run(_ => throw original))(app);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.Build()(new DefaultHttpContext()));
        Assert.True(ReferenceEquals(original, actual));
        using var report = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(_directory).Single()));
        Assert.Equal("EscapedException", report.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("StatusCode").ValueKind);
        Assert.False(report.RootElement.GetRawText().Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_opt_in_no_correlation_header_or_file_is_added()
    {
        using var server = CreateHost(context =>
        {
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        }, enabled: false);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.Headers.Contains(TestFailureCorrelation.ResponseHeader));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Invalid_SQLSTATE_is_not_exported()
    {
        using var server = CreateHost(_ => throw new PostgresException(Sentinel, Sentinel, Sentinel, Sentinel));
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        using var report = ReadReport(response);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("Exceptions")[0].GetProperty("SqlState").ValueKind);
        Assert.False(report.RootElement.GetRawText().Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_relative_missing_and_worktree_output_directories()
    {
        Assert.Throws<ArgumentException>(() => new TestFailureCorrelation("relative"));
        Assert.Throws<ArgumentException>(() => new TestFailureCorrelation(Path.Combine(_directory, "missing")));
        // Find the repository from the test binary, not a developer-specific path.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, ".git")) &&
               !File.Exists(Path.Combine(repo.FullName, ".git")))
            repo = repo.Parent;
        Assert.NotNull(repo);
        Assert.Throws<ArgumentException>(() => new TestFailureCorrelation(repo.FullName));
        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(_directory, "worktree-alias");
            Directory.CreateSymbolicLink(alias, repo.FullName);
            Assert.Throws<ArgumentException>(() => new TestFailureCorrelation(alias));
            Directory.Delete(alias);
        }
    }

    [Fact]
    public async Task Unknown_client_scope_cannot_claim_the_test_output()
    {
        using var server = CreateHost(context =>
        {
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        using var client = CreateClient(server);
        using var bound = await client.GetAsync("/", TestContext.Current.CancellationToken);
        using var boundReport = ReadReport(bound);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(TestFailureCorrelation.ClientHeader, Sentinel);
        using var unbound = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var unboundReport = ReadReport(unbound);
        Assert.Contains(boundReport.RootElement.GetProperty("RequestId").GetString()!,
            TestContext.Current.TestOutputHelper!.Output);
        Assert.DoesNotContain(unboundReport.RootElement.GetProperty("RequestId").GetString()!,
            TestContext.Current.TestOutputHelper.Output);
        Assert.False(unboundReport.RootElement.GetRawText().Contains(Sentinel, StringComparison.Ordinal));
    }

    private IHost CreateHost(RequestDelegate endpoint, bool enabled = true)
    {
        _correlation = enabled ? new TestFailureCorrelation(_directory) : null;
        return new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                if (_correlation is not null) services.AddSingleton<IStartupFilter>(_correlation);
            })
            .Configure(app =>
            {
                // Same ASP.NET exception-handler mechanism as Program, with a
                // fixed body so deliberately toxic exception text never prints.
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = context => context.Response.WriteAsync("handled-body"),
                });
                app.Run(endpoint);
            })).Start();
    }

    private HttpClient CreateClient(IHost server)
    {
        var client = server.GetTestClient();
        _correlation?.AttachClient(client, TestContext.Current.TestOutputHelper);
        return client;
    }

    private JsonDocument ReadReport(HttpResponseMessage response)
    {
        var id = Assert.Single(response.Headers.GetValues(TestFailureCorrelation.ResponseHeader));
        Assert.True(Guid.TryParseExact(id, "N", out _));
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, $"failure-{id}.json")));
        Assert.Equal(id, report.RootElement.GetProperty("RequestId").GetString());
        return report;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
