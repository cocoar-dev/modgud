namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// One Recovery-CLI command. Implementations are small, single-purpose, and
/// stateless — one instance is registered in <see cref="RecoveryCli"/>'s command
/// table. The dispatcher validates the tenant for <see cref="RequiresRealm"/>
/// commands, enters the <c>TenantContext</c>, opens a DI scope, and calls
/// <see cref="ExecuteAsync"/>; each command resolves only the services it needs
/// (so a global command never eagerly opens a tenant session it won't use).
/// </summary>
public interface IRecoveryCommand
{
    /// <summary>The command keyword (e.g. <c>bootstrap-admin</c>), matched case-insensitively.</summary>
    string Name { get; }

    /// <summary>
    /// True when the command acts INSIDE the <c>--realm</c> tenant — the global
    /// <c>--realm</c> is then resolved + validated before the command runs. False
    /// for the global realm-management commands that carry their own <c>--slug</c>
    /// (<c>realm-*</c>, <c>control-plane</c>, <c>adopt-tenant</c>).
    /// </summary>
    bool RequiresRealm { get; }

    /// <summary>Runs the command; returns the process exit code (0 = success, non-zero = failure).</summary>
    Task<int> ExecuteAsync(RecoveryCliContext ctx);
}
