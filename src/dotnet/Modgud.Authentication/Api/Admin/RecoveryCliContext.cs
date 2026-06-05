using Microsoft.AspNetCore.Hosting;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Per-invocation context handed to every <see cref="IRecoveryCommand"/>. Carries
/// the scoped service provider (already inside the resolved tenant's
/// <c>TenantContext</c>), the raw argv, the resolved realm, the host environment,
/// and the output writers. All command output goes through <see cref="Out"/> /
/// <see cref="Error"/> (Console in production, captured writers under test) — no
/// command touches <see cref="System.Console"/> directly, so the CLI is testable
/// without process-global Console redirection.
/// </summary>
public sealed class RecoveryCliContext
{
    public RecoveryCliContext(
        IServiceProvider services,
        string[] args,
        string realmSlug,
        IWebHostEnvironment env,
        TextWriter outWriter,
        TextWriter errorWriter)
    {
        Services = services;
        Args = args;
        RealmSlug = realmSlug;
        Env = env;
        Out = outWriter;
        Error = errorWriter;
    }

    /// <summary>Scoped service provider — already inside the resolved tenant's TenantContext.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Full argv, with <c>Args[0]</c> the command name.</summary>
    public string[] Args { get; }

    /// <summary>Resolved tenant slug (the global <c>--realm</c>, default <c>system</c>).</summary>
    public string RealmSlug { get; }

    public IWebHostEnvironment Env { get; }
    public TextWriter Out { get; }
    public TextWriter Error { get; }

    /// <summary>Reads a <c>--key value</c> / <c>--key=value</c> flag, or null if absent.</summary>
    public string? Flag(string name) => RecoveryCli.ParseFlag(Args, name);

    /// <summary>Writes a line to stdout.</summary>
    public void WriteLine(string text = "") => Out.WriteLine(text);

    /// <summary>Writes <c>error: {message}</c> to stderr and returns exit code 1.</summary>
    public int Fail(string message)
    {
        Error.WriteLine($"error: {message}");
        return 1;
    }

    /// <summary>
    /// Prints the post-mutation restart hint. The CLI runs as a separate process
    /// (<c>docker exec ... recover ...</c>), so the running server's in-process
    /// realm cache only picks up realm changes after a restart.
    /// </summary>
    public void PrintRestartHint()
    {
        Out.WriteLine();
        Out.WriteLine("⚠ Restart the running container so the in-process realm cache");
        Out.WriteLine("  picks up the change (the CLI runs as a separate process):");
        Out.WriteLine();
        Out.WriteLine("    docker compose restart auth   # or your Compose service name");
        Out.WriteLine();
    }
}
