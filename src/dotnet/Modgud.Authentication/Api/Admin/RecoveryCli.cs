using Modgud.Authentication;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Break-glass recovery CLI — for when the last admin locks themselves out of 2FA
/// and nobody else can help from the UI side. Run inside the running container:
///
///   docker exec &lt;container&gt; dotnet Modgud.Api.dll recover list
///   docker exec &lt;container&gt; dotnet Modgud.Api.dll recover reset-2fa &lt;username&gt;
///   docker exec &lt;container&gt; dotnet Modgud.Api.dll recover magic-link &lt;username&gt;
///   docker exec &lt;container&gt; dotnet Modgud.Api.dll recover help
///
/// Requires shell access to the host — anyone who can <c>docker exec</c> already has
/// DB access, so this doesn't open a new privilege-escalation path. Every invocation
/// emits an <c>ops.recovery_cli_invoked</c> record to the streamless security/ops
/// store (flushed synchronously before the process exits, since this path never
/// starts the host) so admins can audit usage after the fact.
///
/// <para>This type is only the dispatcher + shared helpers; each command is its own
/// <see cref="IRecoveryCommand"/> (see RecoveryCommands.cs).</para>
/// </summary>
public static class RecoveryCli
{
    // The command table. One small class per command; the dispatcher looks the
    // command up by name and asks it whether it needs realm resolution.
    private static readonly IReadOnlyList<IRecoveryCommand> AllCommands =
    [
        new ListCommand(),
        new Reset2FaCommand(),
        new SetEmailCommand(),
        new MagicLinkCommand(),
        new RebuildProjectionsCommand(),
        new BootstrapAdminCommand(),
        new MigrateCcCredentialsCommand(),
        new RealmAddDomainCommand(),
        new RealmRemoveDomainCommand(),
        new RealmSetPrimaryDomainCommand(),
        new RealmListCommand(),
        new ControlPlaneCommand(),
        new AdoptTenantCommand(),
        new RotateSigningKeyCommand(),
    ];

    private static readonly Dictionary<string, IRecoveryCommand> ByName =
        AllCommands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Production entry — output goes to the process Console.</summary>
    public static Task<int> RunAsync(IServiceProvider services, string[] args, IServerConfiguration conf, IWebHostEnvironment env)
        => RunAsync(services, args, conf, env, Console.Out, Console.Error);

    /// <summary>
    /// Testable core — output writers are injected so the CLI can be driven
    /// in-process without redirecting the process Console.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string[] args,
        IServerConfiguration conf,
        IWebHostEnvironment env,
        TextWriter outWriter,
        TextWriter errorWriter)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage(outWriter);
            return args.Length == 0 ? 1 : 0;
        }

        var commandName = args[0].ToLowerInvariant();
        if (!ByName.TryGetValue(commandName, out var command))
        {
            errorWriter.WriteLine($"error: Unknown command: {commandName}. Try 'help'.");
            return 1;
        }

        // Resolve the global --realm for tenant-scoped commands. It defaults to
        // "system", but a misspelled --realm must fail with a clear message (not
        // a deep Marten "tenant not found" crash once it enters a non-existent
        // tenant), and an implicit default is announced when more than one realm
        // exists so the operator never silently acts on the wrong tenant — the
        // same silent-tenant class the HTTP path was hardened against. The global
        // realm-management commands (RequiresRealm == false) carry their own
        // --slug and are not validated here.
        var explicitRealm = ParseFlag(args, "--realm");
        var realmSlug = explicitRealm ?? TenantConstants.SystemTenantId;
        if (command.RequiresRealm)
        {
            var realmError = await ResolveRealmAsync(services, explicitRealm, realmSlug, errorWriter);
            if (realmError is not null) return realmError.Value;
        }

        using var _tenant = TenantContext.Enter(realmSlug);
        await using var scope = services.CreateAsyncScope();

        var ctx = new RecoveryCliContext(scope.ServiceProvider, args, realmSlug, env, outWriter, errorWriter);
        return await command.ExecuteAsync(ctx);
    }

    private static void PrintUsage(TextWriter outw)
    {
        outw.WriteLine("""
            Modgud Recovery CLI
            ─────────────────────

            Usage:
              dotnet Modgud.Api.dll recover <command> [args...] [--realm <slug>]

            Commands:
              list                           List all users (UserName · Email · Active · 2FA · Passkeys).
              reset-2fa <username>           Disable TOTP + Email-OTP + delete all Passkeys for user.
              set-email <username> <email>   Update the user's email address (appends UserUpdatedEvent
                                             so projections + SignalR update live).
              magic-link <username>          Generate a one-time login URL and print it.
              rebuild-projections            Rebuild all Marten projections (inline + async).
                                             Bootstrap path for the first migration after
                                             a schema change when no admin can authenticate yet.
              migrate-cc-credentials         Phase-2C retrofit: for every OAuth client that
                                             still has the `client_credentials` grant without
                                             a LinkedServiceAccountId (i.e. seeded or pre-2C
                                             clients), auto-provision a SA named
                                             `legacy.{clientId}` and backfill the link so
                                             the standard SA-managed mutation guard kicks
                                             in. Idempotent — already-linked clients are
                                             skipped; existing legacy.* SAs are re-used.
                                             Optional --realm flag scopes to one tenant
                                             (defaults to "system").
              bootstrap-admin                Create the first admin in a realm. Two modes:
                  --email <email>            Email — required in both modes.
                  [--username <username>]    Username — defaults to the local-part of the email.
                  [--firstname <name>]       Optional.
                  [--lastname  <name>]       Optional.
                  [--password  <password>]   Direct mode: set the password now (validated against
                                             the configured Identity password rules).
                                             Without --password, an Invite-Mode magic link is
                                             generated and printed (and emailed if SMTP is set).
              realm-list                     List every active realm with its slug + domains.
                                             Useful as a first probe after a fresh deploy to see
                                             the system realm's seeded localhost domains.
              realm-add-domain               Add a domain to an active realm's Domains list.
                  --slug <slug>              Required. Typically "system" for the first
                                             production-hostname add after deploy.
                  --domain <hostname>        Required. The Host-header that should route to
                                             this realm. Stored verbatim, case-insensitive
                                             match at request time.
              realm-remove-domain            Remove a domain from an active realm's Domains list.
                  --slug <slug>              Required.
                  --domain <hostname>        Required. No-op if not present. Cannot remove the
                                             realm's PrimaryDomain or its last remaining domain.
              realm-set-primary-domain       Set the realm's canonical public host (PrimaryDomain).
                  --slug <slug>              Required.
                  --domain <hostname>        Required. Must already be in the realm's Domains.
                                             The PrimaryDomain is the host used for ALL outbound
                                             links (magic-link, password-reset, email-verify,
                                             bootstrap-invite, login-provider callbacks) AND as the
                                             WebAuthn RP ID. ⚠ Changing it INVALIDATES every existing
                                             passkey registered for the realm.
              control-plane list             Show which realm currently holds the control-plane role.
              control-plane transfer <slug>  Move the control-plane role to another realm. Break-glass
                                             for when the control-plane realm has no usable admin —
                                             the target realm's existing realm:admin gains cross-realm
                                             administration. Restart the running container afterwards
                                             (in-process realm cache).
              adopt-tenant <slug> <name> [domain]
                                             Register an ALREADY-EXISTING tenant database
                                             ({master}_{slug}) as a realm — no CREATE DATABASE.
                                             Migration counterpart to creating a realm via the API.
              rotate-signing-key             Rotate the realm's OpenIddict signing key. Generates a
                                             fresh RSA keypair and retires the previous active key
                                             into a 30-day verification overlap window so in-flight
                                             tokens stay valid. Honors --realm (defaults to "system").
              help                           Show this message.

            Global flag:
              --realm <slug>                 Tenant slug to act in. Defaults to "system". Applies to
                                             tenant-scoped commands (bootstrap-admin, list, reset-2fa,
                                             …). A misspelled --realm fails fast with a clear error;
                                             an omitted --realm is announced when more than one realm
                                             exists. The realm-* / control-plane / adopt-tenant commands
                                             carry their own --slug and ignore it.

            Exit codes: 0 on success, non-zero on any failure (validation error,
            unknown realm, unknown command). Error text is written to stderr.

            All commands run against the configured database. No network access (except SMTP
            for Invite-Mode bootstrap-admin). Every invocation is written to the auth log.
            """);
    }

    /// <summary>
    /// Reads <c>--key value</c> pairs out of <paramref name="args"/>. Both
    /// <c>--key value</c> and <c>--key=value</c> forms are accepted. Returns null
    /// when the flag isn't set; returns the empty string when the flag is set with
    /// an empty value (let the caller decide how to react).
    /// </summary>
    internal static string? ParseFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : "";
            }
            var prefix = flag + "=";
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return a[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// Validates the global <c>--realm</c> for tenant-scoped commands. Returns a
    /// non-null exit code to short-circuit <see cref="RunAsync"/> when the named
    /// realm doesn't exist — a misspelled <c>--realm</c> must fail loudly with a
    /// clear message instead of entering a tenant that doesn't exist (which would
    /// surface as a deep Marten error). Announces the implicit <c>system</c>
    /// default only when more than one active realm exists, so single-tenant
    /// operators aren't nagged but a multi-realm operator can never silently act
    /// on the wrong tenant.
    /// </summary>
    private static async Task<int?> ResolveRealmAsync(
        IServiceProvider services, string? explicitRealm, string realmSlug, TextWriter errorWriter)
    {
        var globalStore = services.GetRequiredService<IGlobalStore>();
        await using var globalSession = globalStore.QuerySession();
        var activeRealms = await globalSession.Query<Realm>()
            .Where(r => r.IsActive)
            .ToListAsync();

        // Case-sensitive match: the tenant registry is keyed by the exact slug,
        // so "System" is genuinely not a realm and must error rather than enter a
        // tenant that doesn't exist.
        if (!activeRealms.Any(r => string.Equals(r.Slug, realmSlug, StringComparison.Ordinal)))
        {
            errorWriter.WriteLine(explicitRealm is not null
                ? $"error: Realm '{realmSlug}' not found. Run 'recover realm-list' to see available realms."
                : $"error: The '{realmSlug}' realm does not exist yet — has the deployment been bootstrapped? Run 'recover realm-list'.");
            return 1;
        }

        if (explicitRealm is null && activeRealms.Count > 1)
        {
            errorWriter.WriteLine(
                $"note: no --realm specified; acting on the '{realmSlug}' realm " +
                $"({activeRealms.Count} active realms exist — pass --realm <slug> to target another).");
        }

        return null;
    }
}
