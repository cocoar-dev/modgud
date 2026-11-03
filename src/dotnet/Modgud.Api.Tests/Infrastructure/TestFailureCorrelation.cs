using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Opt-in test-host observer. No process-wide exception hook, logger state,
/// request values, exception messages or response bodies enter the report.
/// </summary>
internal sealed class TestFailureCorrelation : IStartupFilter
{
    internal const string DirectoryVariable = "MODGUD_TEST_FAILURE_DIAGNOSTICS_DIRECTORY";
    internal const string ResponseHeader = "X-Modgud-Test-Failure-Id";
    internal const string ClientHeader = "X-Modgud-Test-Output-Scope";
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, ITestOutputHelper> _testOutputs = new();

    public TestFailureCorrelation(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Diagnostics require an existing absolute directory outside a git worktree.");

        // Resolve parent symlinks too (e.g. /var on macOS), so an external
        // alias of a worktree cannot accidentally receive diagnostic files.
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)!;
        var target = new DirectoryInfo(root);
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var child = new DirectoryInfo(Path.Combine(target.FullName, part));
            target = child.ResolveLinkTarget(true) as DirectoryInfo ?? child;
        }
        for (var parent = target; parent is not null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) ||
                File.Exists(Path.Combine(parent.FullName, ".git")))
                throw new ArgumentException("Diagnostics must remain outside a git worktree.");
        _directory = target.FullName;
    }

    internal void AttachClient(HttpClient client, ITestOutputHelper? output)
    {
        if (output is null) return;
        var scope = Guid.NewGuid().ToString("N");
        _testOutputs[scope] = output;
        client.DefaultRequestHeaders.Remove(ClientHeader);
        client.DefaultRequestHeaders.Add(ClientHeader, scope);
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(downstream => async context =>
        {
            // Never trust client headers, trace IDs, URLs or account identifiers.
            var requestId = Guid.NewGuid().ToString("N");
            // TestServer deliberately does not flow the caller's ExecutionContext.
            // Associate only scopes minted by this observer, not arbitrary headers.
            ITestOutputHelper? output = null;
            var scope = context.Request.Headers[ClientHeader];
            if (scope.Count == 1 && scope[0] is { } key)
                _testOutputs.TryGetValue(key, out output);
            var recorded = false;
            void Record(string outcome, int? status, Exception? error)
            {
                if (recorded) return;
                recorded = true;
                TryWrite(requestId, outcome, status, error, output);
            }

            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status500InternalServerError)
                {
                    context.Response.Headers[ResponseHeader] = requestId;
                    Record("Http500", context.Response.StatusCode,
                        context.Features.Get<IExceptionHandlerFeature>()?.Error);
                }
                return Task.CompletedTask;
            });

            try
            {
                await downstream(context);
            }
            catch (Exception error)
            {
                // Preserve the original exception/transport outcome, not a fabricated 500.
                Record("EscapedException", null, error);
                throw;
            }
            finally
            {
                // Also cover 500 responses that haven't started writing yet.
                if (context.Response.StatusCode == StatusCodes.Status500InternalServerError)
                    Record("Http500", context.Response.StatusCode,
                        context.Features.Get<IExceptionHandlerFeature>()?.Error);
            }
        });
        next(app);
    };

    private void TryWrite(string requestId, string outcome, int? status, Exception? error, ITestOutputHelper? output)
    {
        try
        {
            var exceptions = new List<ExceptionMetadata>();
            for (var current = error; current is not null && exceptions.Count < 16; current = current.InnerException)
            {
                var frames = new StackTrace(current, false).GetFrames().Take(64)
                    .Select(frame => frame.GetMethod())
                    .Where(method => method is not null)
                    .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
                    .ToArray();
                var state = current is PostgresException pg &&
                            pg.SqlState.Length == 5 &&
                            pg.SqlState.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'Z')
                    ? pg.SqlState : null;
                exceptions.Add(new ExceptionMetadata(current.GetType().FullName!, state, frames));
            }

            var report = new FailureMetadata(requestId, outcome, status, exceptions.ToArray());
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(Path.Combine(_directory, $"failure-{requestId}.json"), options);
            JsonSerializer.Serialize(file, report);
        }
        catch
        {
            // Diagnostic I/O/serialization must never replace a response or exception.
            // No exception text is safe to print here either.
            WriteOutput(output, $"MODGUD_TEST_FAILURE_WRITE_FAILED {requestId}");
            return;
        }
        WriteOutput(output, $"MODGUD_TEST_FAILURE {requestId} {outcome}");
    }

    private static void WriteOutput(ITestOutputHelper? output, string marker)
    {
        try { output?.WriteLine(marker); }
        catch { /* A disposed output helper must not affect HTTP either. */ }
    }

    private sealed record FailureMetadata(
        string RequestId, string Outcome, int? StatusCode, ExceptionMetadata[] Exceptions);
    private sealed record ExceptionMetadata(string Type, string? SqlState, string[] Methods);
}
