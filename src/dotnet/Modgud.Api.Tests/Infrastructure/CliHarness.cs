using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication;
using Modgud.Authentication.Api.Admin;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>Captured result of one in-process Recovery-CLI invocation.</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>stdout + stderr combined — convenient for substring asserts.</summary>
    public string All => StdOut + StdErr;
}

/// <summary>
/// In-process harness for the Recovery CLI (Stage 1 of the cold-start ladder).
/// Drives the real <see cref="RecoveryCli.RunAsync"/> against a booted host's DI
/// container — the same execution model production uses (Program.cs boots the
/// host, then dispatches <c>recover &lt;cmd&gt;</c>) — and captures stdout, stderr,
/// and the exit code. Resulting events/documents are asserted by the test
/// against the same host's stores.
///
/// <para>Console redirection is process-global; CLI tests therefore run in the
/// non-parallel cold-start collection.</para>
/// </summary>
public static class CliHarness
{
    public static async Task<CliResult> RunAsync(IServiceProvider services, params string[] args)
    {
        var conf = services.GetRequiredService<IServerConfiguration>();
        var env = services.GetRequiredService<IWebHostEnvironment>();

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);

            var exitCode = await RecoveryCli.RunAsync(services, args, conf, env);

            return new CliResult(exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
