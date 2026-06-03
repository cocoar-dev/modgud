using System.Security.Cryptography;
using System.Text;
using Modgud.Authentication;
using Modgud.Authentication.Setup;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Projections;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Permissions;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Domain.Users.Events;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Projections;


namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Break-glass recovery CLI — for when the last admin locks themselves out of 2FA and
/// nobody else can help from the UI side. Run inside the running container:
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
/// </summary>
public static class RecoveryCli
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, IServerConfiguration conf, IWebHostEnvironment env)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();

        // bootstrap-admin runs against a chosen realm (default: system).
        // The CLI's filesystem-trust boundary lets the operator name the
        // tenant; everything else uses TenantContext fallback ("system").
        var realmSlug = ParseFlag(args, "--realm") ?? TenantConstants.SystemTenantId;
        using var _tenant = TenantContext.Enter(realmSlug);

        await using var scope = services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        var securityAudit = scope.ServiceProvider.GetRequiredService<ISecurityAuditLog>();

        return command switch
        {
            "list" => await ListUsersAsync(session, permissions),
            "reset-2fa" => await Reset2FaAsync(session, userManager, args, securityAudit, realmSlug),
            "set-email" => await SetEmailAsync(session, userManager, args, securityAudit, realmSlug),
            "magic-link" => await MagicLinkAsync(session, scope.ServiceProvider, args, conf, env),
            "rebuild-projections" => await RebuildProjectionsAsync(scope.ServiceProvider, realmSlug),
            "bootstrap-admin" => await BootstrapAdminAsync(scope.ServiceProvider, args, realmSlug),
            "migrate-cc-credentials" => await MigrateClientCredentialsAsync(scope.ServiceProvider, args, realmSlug),
            "realm-add-domain" => await RealmAddDomainAsync(scope.ServiceProvider, args),
            "realm-remove-domain" => await RealmRemoveDomainAsync(scope.ServiceProvider, args),
            "realm-list" => await RealmListAsync(scope.ServiceProvider),
            "control-plane" => await ControlPlaneAsync(scope.ServiceProvider, args),
            "adopt-tenant" => await AdoptTenantAsync(scope.ServiceProvider, args),
            "rotate-signing-key" => await RotateSigningKeyAsync(scope.ServiceProvider, realmSlug),
            _ => Error($"Unknown command: {command}. Try 'help'.")
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
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
                  --domain <hostname>        Required. No-op if not present.
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
              --realm <slug>                 Tenant slug to act in. Defaults to "system".
                                             Applies to bootstrap-admin (and any future tenant-
                                             scoped command). Other commands always run in the
                                             tenant the configured connection points at.

            All commands run against the configured database. No network access (except SMTP
            for Invite-Mode bootstrap-admin). Every invocation is written to the auth log.
            """);
    }

    // ── list ────────────────────────────────────────────────────────────

    private static async Task<int> ListUsersAsync(IDocumentSession session, IPermissionService permissions)
    {
        var users = await session.Query<ApplicationUser>()
            .Where(u => !u.IsDeleted)
            .OrderBy(u => u.UserName)
            .ToListAsync();

        Console.WriteLine($"{"UserName",-20} {"Email",-40} {"Active",-8} {"Admin",-7} {"2FA",-6} {"Passkeys"}");
        Console.WriteLine(new string('─', 100));

        foreach (var user in users)
        {
            var passkeyCount = await session.Query<StoredPasskeyCredential>()
                .Where(p => p.UserId == user.Id)
                .CountAsync();

            var mfa = user.TwoFactorEnabled ? "TOTP" : (user.EmailOtpEnabled ? "EMAIL" : "-");
            // "Admin" in the recovery CLI = realm-wide admin: the user holds
            // realm:admin (typically via the System Admin role + Administratoren
            // group seeded at /setup). The check is run against any app slug
            // because realm:admin is universal — modgud picked here for
            // legibility only.
            var isAdmin = await permissions.HasPermissionAsync(user.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, CancellationToken.None);

            Console.WriteLine(
                $"{user.UserName,-20} {user.Email ?? "",-40} {(user.IsActive ? "yes" : "no"),-8} " +
                $"{(isAdmin ? "yes" : "no"),-7} {mfa,-6} {passkeyCount}");
        }

        return 0;
    }

    // ── reset-2fa ───────────────────────────────────────────────────────

    private static async Task<int> Reset2FaAsync(
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        string[] args,
        ISecurityAuditLog securityAudit,
        string realmSlug)
    {
        if (args.Length < 2) return Error("Usage: recover reset-2fa <username>");
        var userName = args[1].Trim().ToLowerInvariant();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return Error($"User not found: {userName}");

        var wasTotpEnabled = user.TwoFactorEnabled;
        var wasEmailOtpEnabled = user.EmailOtpEnabled;

        // Disable TOTP — clears AuthenticatorKey in the UserSecurityData doc via Identity.
        if (wasTotpEnabled)
        {
            await userManager.SetTwoFactorEnabledAsync(user, false);
            await userManager.ResetAuthenticatorKeyAsync(user);
        }

        // Disable Email-OTP — flag on ApplicationUser document.
        if (wasEmailOtpEnabled)
        {
            user.EmailOtpEnabled = false;
            session.Store(user);
        }

        // Delete all passkeys for this user.
        var passkeys = await session.Query<StoredPasskeyCredential>()
            .Where(p => p.UserId == user.Id)
            .ToListAsync();
        foreach (var passkey in passkeys)
            session.Delete(passkey);

        // Reset grace-period stamp so the user gets a fresh window on next login.
        var security = await session.LoadAsync<UserSecurityData>(user.Id);
        if (security is not null && security.SecureSetupDueAt is not null)
        {
            security.SecureSetupDueAt = null;
            session.Store(security);
        }

        await session.SaveChangesAsync();

        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = realmSlug,
            Actor = user.UserName,
            Status = "succeeded",
            Reason = $"reset-2fa: User={user.UserName} TOTP={wasTotpEnabled} EmailOtp={wasEmailOtpEnabled} PasskeysDeleted={passkeys.Count}",
            Message = $"Recovery reset-2fa. User={user.UserName} TOTP={wasTotpEnabled} EmailOtp={wasEmailOtpEnabled} PasskeysDeleted={passkeys.Count}",
        });

        Console.WriteLine($"✓ 2FA reset for {user.UserName}:");
        Console.WriteLine($"  TOTP disabled:    {(wasTotpEnabled ? "yes" : "was already off")}");
        Console.WriteLine($"  Email-OTP off:    {(wasEmailOtpEnabled ? "yes" : "was already off")}");
        Console.WriteLine($"  Passkeys deleted: {passkeys.Count}");
        Console.WriteLine($"  Grace period:    reset (fresh window on next login)");
        return 0;
    }

    // ── set-email ───────────────────────────────────────────────────────

    private static async Task<int> SetEmailAsync(
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        string[] args,
        ISecurityAuditLog securityAudit,
        string realmSlug)
    {
        if (args.Length < 3) return Error("Usage: recover set-email <username> <new-email>");
        var userName = args[1].Trim().ToLowerInvariant();
        var newEmail = args[2].Trim();

        if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains('@'))
            return Error($"Invalid email address: {newEmail}");

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return Error($"User not found: {userName}");

        // Uniqueness guard — same check UpdateUserCommand runs. The polymorphic
        // Principal projection is inline, so this is strongly consistent.
        var normalizedEmail = newEmail.ToUpperInvariant();
        var personConflict = await session.Query<Modgud.Authorization.Principals.Person>()
            .Where(p => p.NormalizedEmail == normalizedEmail && p.Id != user.Id && !p.IsDeleted)
            .AnyAsync();
        var groupConflict = await session.Query<Group>()
            .Where(g => g.Email != null && g.Email.ToUpper() == normalizedEmail && !g.IsDeleted)
            .AnyAsync();
        if (personConflict || groupConflict)
            return Error($"Email already in use by another principal: {newEmail}");

        var oldEmail = user.Email;

        // Update the Identity document + append the domain event in the same transaction,
        // so the UserView projection + label sync handlers + SignalR dispatch catch up
        // without a race window.
        user.Email = newEmail;
        user.NormalizedEmail = newEmail.ToUpperInvariant();
        session.Store(user);

        session.Events.Append(user.Id, new UserUpdatedEvent(
            Id: user.Id,
            Firstname: default,
            Lastname: default,
            Acronym: default,
            Email: newEmail));

        await session.SaveChangesAsync();

        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = realmSlug,
            Actor = user.UserName,
            Status = "succeeded",
            Reason = $"set-email: User={user.UserName} Old={LogPiiMasking.MaskEmail(oldEmail)} New={LogPiiMasking.MaskEmail(newEmail)}",
            Message = $"Recovery set-email. User={user.UserName} Old={LogPiiMasking.MaskEmail(oldEmail)} New={LogPiiMasking.MaskEmail(newEmail)}",
        });

        Console.WriteLine($"✓ Email updated for {user.UserName}:");
        Console.WriteLine($"  Old: {oldEmail ?? "(none)"}");
        Console.WriteLine($"  New: {newEmail}");
        return 0;
    }

    // ── magic-link ──────────────────────────────────────────────────────

    private static async Task<int> MagicLinkAsync(
        IDocumentSession session,
        IServiceProvider scopedServices,
        string[] args,
        IServerConfiguration conf,
        IWebHostEnvironment env)
    {
        if (args.Length < 2) return Error("Usage: recover magic-link <username>");
        var userName = args[1].Trim().ToLowerInvariant();

        var userManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return Error($"User not found: {userName}");
        if (!user.IsActive) return Error($"User is inactive: {userName}");

        // Clear old challenges + create a fresh one (same pattern as AdminMagicLinkEndpoints).
        var existing = await session.Query<MagicLinkChallenge>()
            .Where(c => c.UserId == user.Id)
            .ToListAsync();
        foreach (var old in existing)
            session.Delete(old);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var expirationMinutes = scopedServices.GetService<IMagicLinkConfiguration>()?.ExpirationMinutes ?? 15;

        var challenge = new MagicLinkChallenge
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expirationMinutes),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(challenge);
        await session.SaveChangesAsync();

        var appUrl = (conf.PublicUrl ?? (env.IsDevelopment() ? "http://localhost:4300" : conf.AppUrl)).TrimEnd('/');
        var url = $"{appUrl}/magic-login?userId={user.Id}&token={Uri.EscapeDataString(token)}";

        scopedServices.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Actor = user.UserName,
            Status = "succeeded",
            Reason = $"magic-link: User={user.UserName} ExpiresAt={challenge.ExpiresAt:O}",
            Message = $"Recovery magic-link generated. User={user.UserName} ExpiresAt={challenge.ExpiresAt:O}",
        });

        Console.WriteLine($"✓ Magic link for {user.UserName} (expires in {expirationMinutes} min):");
        Console.WriteLine();
        Console.WriteLine($"  {url}");
        Console.WriteLine();
        Console.WriteLine("Open in a browser — single use, 2FA bypassed.");
        return 0;
    }

    // ── rebuild-projections ─────────────────────────────────────────────

    /// <summary>
    /// Rebuilds all Marten projections from event 0. Mirrors what the admin
    /// rebuild endpoint does, but runs without auth — needed when a schema
    /// change leaves <c>mt_doc_principal</c> empty so no user can claim
    /// <c>app:admin</c> until the principal projection is replayed.
    /// </summary>
    private static async Task<int> RebuildProjectionsAsync(IServiceProvider services, string tenantId)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        var timeout = TimeSpan.FromMinutes(10);

        Console.WriteLine("Rebuilding Marten projections...");
        var securityAudit = services.GetRequiredService<ISecurityAuditLog>();
        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = tenantId,
            Status = "initiated",
            Reason = "rebuild-projections",
            Message = "Recovery rebuild-projections initiated",
        });

        // MasterTableTenancy disables Marten's default tenant, so the no-arg
        // overload throws DefaultTenantUsageDisabledException — build the daemon
        // for the resolved realm's DB explicitly (honors --realm; default system).
        using var daemon = await store.BuildProjectionDaemonAsync(tenantId);
        await daemon.RebuildProjectionAsync("ViewProjections", timeout, CancellationToken.None);
        Console.WriteLine("  OK ViewProjections");

        await daemon.RebuildProjectionAsync<ModgudPrincipalProjection>(timeout, CancellationToken.None);
        Console.WriteLine("  OK ModgudPrincipalProjection (mt_doc_principal)");

        await daemon.RebuildProjectionAsync<PermissionRoleProjection>(timeout, CancellationToken.None);
        Console.WriteLine("  OK PermissionRoleProjection (mt_doc_permissionrole)");

        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = tenantId,
            Status = "succeeded",
            Reason = "rebuild-projections",
            Message = "Recovery rebuild-projections completed",
        });
        return 0;
    }

    // ── bootstrap-admin ─────────────────────────────────────────────────

    /// <summary>
    /// First-admin creation for a realm. Two modes selected by whether
    /// <c>--password</c> is provided:
    /// <list type="bullet">
    ///   <item><description>Direct mode (<c>--password</c> set): atomic
    ///   user + role + group seed via <see cref="IRealmAdminBootstrapper"/>.
    ///   Identity-Password-Rules are enforced — a weak password is rejected
    ///   the same way the SPA login form would reject it. The CLI's
    ///   filesystem-trust does NOT bypass policy; the bypass would create
    ///   a slow-burn liability where someone forgets a 4-char password
    ///   on a Production deployment.</description></item>
    ///   <item><description>Invite mode (no <c>--password</c>): a
    ///   <c>PendingAdminInvite</c> document is written and a magic-link
    ///   URL is printed (and emailed when SMTP is configured). The
    ///   recipient sets their password via the SPA bootstrap form.
    ///   Built in C15b — until then this branch errors with "not yet
    ///   implemented".</description></item>
    /// </list>
    /// Tenant scoping: <c>--realm &lt;slug&gt;</c> is read in
    /// <see cref="RunAsync"/> and propagated via <c>TenantContext</c>; the
    /// <see cref="IDocumentSession"/> resolved here is already
    /// realm-scoped.
    /// </summary>
    private static async Task<int> BootstrapAdminAsync(
        IServiceProvider scopedServices,
        string[] args,
        string realmSlug)
    {
        var email = ParseFlag(args, "--email");
        if (string.IsNullOrWhiteSpace(email))
            return Error("Usage: recover bootstrap-admin --email <email> [--username <name>] [--firstname <name>] [--lastname <name>] [--password <pw>] [--realm <slug>]");

        var userName = ParseFlag(args, "--username")?.Trim().ToLowerInvariant()
            ?? email.Split('@', 2)[0].ToLowerInvariant();
        var firstname = ParseFlag(args, "--firstname");
        var lastname = ParseFlag(args, "--lastname");
        var password = ParseFlag(args, "--password");

        if (string.IsNullOrEmpty(password))
        {
            // Invite mode: write a PendingAdminInvite + send email + print
            // the magic-link URL on stdout (also useful when SMTP isn't
            // configured locally — operator can copy/paste).
            return await BootstrapAdminInviteAsync(scopedServices, realmSlug, userName, email, firstname, lastname);
        }

        var bootstrapper = scopedServices.GetRequiredService<IRealmAdminBootstrapper>();
        var result = await bootstrapper.BootstrapDirectAsync(userName, password, email, firstname, lastname);

        var securityAudit = scopedServices.GetRequiredService<ISecurityAuditLog>();
        if (result.IsError)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.RecoveryCliInvoked,
                Level = "Warning",
                Realm = realmSlug,
                Actor = userName,
                Status = "failed",
                Reason = $"bootstrap-admin: UserName={userName} Code={result.FirstError.Code} Detail={result.FirstError.Description}",
                Message = $"Recovery bootstrap-admin failed. Realm={realmSlug} UserName={userName} Code={result.FirstError.Code} Detail={result.FirstError.Description}",
            });
            return Error($"{result.FirstError.Code}: {result.FirstError.Description}");
        }

        var admin = result.Value;
        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = realmSlug,
            Actor = admin.UserName,
            Status = "succeeded",
            Reason = $"bootstrap-admin: UserName={admin.UserName} Mode=Direct",
            Message = $"Recovery bootstrap-admin succeeded. Realm={realmSlug} UserName={admin.UserName} Mode=Direct",
        });

        Console.WriteLine($"✓ Admin created in realm '{realmSlug}':");
        Console.WriteLine($"  UserName: {admin.UserName}");
        Console.WriteLine($"  Email:    {admin.Email}");
        Console.WriteLine($"  Mode:     Direct (password set on creation)");
        Console.WriteLine();
        Console.WriteLine("Sign in via the realm's domain — the user is in the Administratoren group with realm:admin.");
        return 0;
    }

    private static async Task<int> BootstrapAdminInviteAsync(
        IServiceProvider scopedServices,
        string realmSlug,
        string userName,
        string email,
        string? firstname,
        string? lastname)
    {
        // Look up the realm in the global store — the invite service needs
        // the DisplayName + Domains[] for the email template + magic-link
        // URL. The CLI's --realm flag carried us into the right tenant
        // session via TenantContext, but the Realm document itself lives
        // in IGlobalStore.
        var globalStore = scopedServices.GetRequiredService<IGlobalStore>();
        await using var globalSession = globalStore.QuerySession();
        var realm = await globalSession.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == realmSlug);
        if (realm is null)
            return Error($"Realm '{realmSlug}' not found.");

        var inviteService = scopedServices.GetRequiredService<IPendingAdminInviteService>();
        var invite = await inviteService.IssueAsync(
            userName, email, firstname, lastname,
            issuedBy: null, // CLI invocation — no authenticated CP-admin
            realm);

        scopedServices.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = realmSlug,
            Actor = userName,
            Status = "initiated",
            Reason = $"bootstrap-admin invite: UserName={userName} Email={LogPiiMasking.MaskEmail(email)} ExpiresAt={invite.ExpiresAt:O}",
            Message = $"Recovery bootstrap-admin issued invite. Realm={realmSlug} UserName={userName} Email={LogPiiMasking.MaskEmail(email)} ExpiresAt={invite.ExpiresAt:O}",
        });

        Console.WriteLine($"✓ Bootstrap-invite issued for realm '{realmSlug}':");
        Console.WriteLine($"  UserName:  {invite.UserName}");
        Console.WriteLine($"  Email:     {invite.Email}");
        Console.WriteLine($"  Expires:   {invite.ExpiresAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine();
        Console.WriteLine($"  Link:      {invite.MagicLinkUrl}");
        Console.WriteLine();
        Console.WriteLine("Recipient opens the link, sets a password, signs in. The link is single-use.");
        return 0;
    }

    /// <summary>
    /// Reads <c>--key value</c> pairs out of <paramref name="args"/>. Both
    /// <c>--key value</c> and <c>--key=value</c> forms are accepted.
    /// Returns null when the flag isn't set; returns the empty string
    /// when the flag is set with an empty value (let the caller decide
    /// how to react).
    /// </summary>
    private static string? ParseFlag(string[] args, string flag)
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

    // ── realm-list / realm-add-domain / realm-remove-domain ────────────
    //
    // These three operate on the global store (Marten master DB) directly:
    // realms are global metadata, not tenant-scoped. We don't go through
    // the IRealmProvisioningService.UpdateRealmAsync path because (a) it
    // requires building an UpdateRealmDto with the full new Domains list
    // (CLI gets to be additive) and (b) the recovery CLI's job is to
    // unstick a deployment, not to honour every endpoint guard.

    // ── migrate-cc-credentials ──────────────────────────────────────────
    //
    // Phase-2C retrofit. Pre-2C dev seeds + any legacy production data may
    // still hold `client_credentials`-grant OAuth clients without a
    // LinkedServiceAccountId — that combination violates the SA-link
    // invariant (R1) introduced in Step 2 and won't survive any standard
    // PUT/DELETE/regenerate-secret path (the standard endpoints all reject
    // SA-managed-or-not-yet-linked cc clients via the invariant). The
    // migration walks every such client and:
    //
    //   1. Picks (or re-uses) a ServiceAccount named `legacy.{clientId}`.
    //      The pattern keeps the audit trail self-documenting — every SA
    //      coming from migration has a name that points back at the
    //      original client_id.
    //   2. Emits OAuthApplicationServiceAccountLinkChanged on the client's
    //      event stream so the projection picks up the new link and the
    //      token endpoint resolves sub = SA.Id from the next request on.
    //
    // Idempotent: clients that are already linked are skipped; if a
    // `legacy.{clientId}` SA already exists from a previous run, it's
    // re-used (looked up by name).

    private static async Task<int> MigrateClientCredentialsAsync(
        IServiceProvider scopedServices, string[] args, string realmSlug)
    {
        _ = args; // no flags beyond the global --realm parsed in RunAsync
        var session = scopedServices.GetRequiredService<IDocumentSession>();

        // Find every cc-grant OAuth client without a SA link in this tenant.
        // The grant lives encoded in Permissions as "gt:client_credentials"
        // (see OAuthPermissions.GrantTypes.ClientCredentials); avoid string-
        // matching it by querying via the constant.
        var unlinked = await session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted
                     && x.LinkedServiceAccountId == null
                     && x.Permissions.Contains(OAuthPermissions.GrantTypes.ClientCredentials))
            .ToListAsync();

        if (unlinked.Count == 0)
        {
            Console.WriteLine($"✓ No client_credentials clients need migration in realm '{realmSlug}'.");
            return 0;
        }

        Console.WriteLine($"Found {unlinked.Count} unlinked client_credentials client(s) in realm '{realmSlug}'.");

        var migrated = 0;
        var saReused = 0;
        var saCreated = 0;

        foreach (var client in unlinked)
        {
            // Normalize the SA name: `legacy.{clientId}` lowercased. ClientIds
            // come in many shapes (dots, hyphens, underscores) so we sanitize
            // anything outside the AccountName charset down to a hyphen.
            var sanitized = SanitizeForAccountName(client.ClientId);
            var saName = $"legacy.{sanitized}";

            // Re-use an existing legacy.* SA if one is already there from a
            // prior run. Polymorphic Marten query — works because
            // ServiceAccount is registered as a Principal subclass.
            var existingSa = await session.Query<ServiceAccount>()
                .FirstOrDefaultAsync(s => !s.IsDeleted && s.AccountName == saName);

            ServiceAccount sa;
            if (existingSa is not null)
            {
                sa = existingSa;
                saReused++;
            }
            else
            {
                sa = new ServiceAccount
                {
                    Id = Guid.NewGuid(),
                    AccountName = saName,
                    Purpose = $"Auto-provisioned by migrate-cc-credentials for OAuth client '{client.ClientId}'.",
                    IsActive = true,
                };
                session.Store(sa);
                saCreated++;
            }

            // Emit the link event on the client's stream so the projection
            // picks up LinkedServiceAccountId. We pass through the aggregate
            // for symmetry with the rest of the codebase — the aggregate's
            // SetLinkedServiceAccountId both produces the event and updates
            // its own state so the next read is consistent.
            var aggregate = await session.Events
                .AggregateStreamAsync<OAuthApplicationAggregate>(client.Id);
            if (aggregate is null || aggregate.IsDeleted)
            {
                // Soft-deleted in the meantime — skip; the cleanup is the
                // operator's job, the migration just unsticks the linkable.
                continue;
            }

            session.Events.Append(client.Id, aggregate.SetLinkedServiceAccountId(sa.Id));
            migrated++;

            Console.WriteLine($"  → {client.ClientId}  →  SA '{saName}'");
        }

        await session.SaveChangesAsync();

        scopedServices.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = realmSlug,
            Status = "succeeded",
            Reason = $"migrate-cc-credentials: Migrated={migrated} SaCreated={saCreated} SaReused={saReused}",
            Message = $"Recovery migrate-cc-credentials completed. Realm={realmSlug} Migrated={migrated} SaCreated={saCreated} SaReused={saReused}",
        });

        Console.WriteLine();
        Console.WriteLine($"✓ Done. Migrated={migrated}  ServiceAccounts created={saCreated}  re-used={saReused}");
        return 0;
    }

    /// <summary>
    /// Coerce an OAuth client_id into the SA AccountName charset
    /// (<c>^[a-z0-9][a-z0-9._-]{1,63}$</c>). Lowercase + replace anything
    /// outside the allowed set with a hyphen. Truncates to 56 chars so the
    /// resulting <c>legacy.{...}</c> still fits the 64-char total budget.
    /// </summary>
    private static string SanitizeForAccountName(string clientId)
    {
        var sb = new StringBuilder(clientId.Length);
        foreach (var ch in clientId.ToLowerInvariant())
        {
            sb.Append(ch is >= 'a' and <= 'z'
                       or >= '0' and <= '9'
                       or '.' or '-' or '_'
                ? ch : '-');
        }
        var s = sb.ToString().Trim('-', '.', '_');
        if (s.Length == 0) s = "client";
        if (s.Length > 56) s = s[..56];
        return s;
    }

    private static async Task<int> RealmListAsync(IServiceProvider services)
    {
        var globalStore = services.GetRequiredService<Modgud.Infrastructure.Persistence.Tenancy.IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realms = await session.Query<Realm>()
            .Where(r => r.IsActive)
            .OrderBy(r => r.Slug)
            .ToListAsync();

        Console.WriteLine($"{"Slug",-20} {"DisplayName",-30} {"Domains"}");
        Console.WriteLine(new string('─', 90));
        foreach (var r in realms)
        {
            var cpMarker = r.IsControlPlane ? " [CP]" : "";
            Console.WriteLine($"{r.Slug + cpMarker,-20} {r.DisplayName,-30} {string.Join(", ", r.Domains)}");
        }
        return 0;
    }

    private static async Task<int> RealmAddDomainAsync(IServiceProvider services, string[] args)
    {
        var slug = ParseFlag(args, "--slug");
        var domain = ParseFlag(args, "--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return Error("realm-add-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = services.GetRequiredService<Modgud.Infrastructure.Persistence.Tenancy.IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug);
        if (realm is null) return Error($"Realm '{slug}' not found.");
        if (!realm.IsActive) return Error($"Realm '{slug}' is not active.");

        if (realm.Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Realm '{slug}' already has domain '{domain}'. No change.");
            return 0;
        }

        realm.Domains = [.. realm.Domains, domain];
        realm.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(realm);
        await session.SaveChangesAsync();

        Console.WriteLine($"✓ Added '{domain}' to realm '{slug}'. Now: [{string.Join(", ", realm.Domains)}]");
        PrintRestartHint();
        services.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = slug,
            Status = "succeeded",
            Reason = $"realm-add-domain: Realm={slug} Domain={domain}",
            Message = $"Recovery realm-add-domain — Realm={slug} Domain={domain}",
        });
        return 0;
    }

    private static async Task<int> RealmRemoveDomainAsync(IServiceProvider services, string[] args)
    {
        var slug = ParseFlag(args, "--slug");
        var domain = ParseFlag(args, "--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return Error("realm-remove-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = services.GetRequiredService<Modgud.Infrastructure.Persistence.Tenancy.IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug);
        if (realm is null) return Error($"Realm '{slug}' not found.");

        var remaining = realm.Domains
            .Where(d => !string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == realm.Domains.Length)
        {
            Console.WriteLine($"Realm '{slug}' did not have domain '{domain}'. No change.");
            return 0;
        }

        realm.Domains = remaining;
        realm.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(realm);
        await session.SaveChangesAsync();

        Console.WriteLine($"✓ Removed '{domain}' from realm '{slug}'. Now: [{string.Join(", ", remaining)}]");
        PrintRestartHint();
        services.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = slug,
            Status = "succeeded",
            Reason = $"realm-remove-domain: Realm={slug} Domain={domain}",
            Message = $"Recovery realm-remove-domain — Realm={slug} Domain={domain}",
        });
        return 0;
    }

    // ── control-plane ───────────────────────────────────────────────────
    //
    // Break-glass to inspect or relocate the control-plane role. Operates on
    // the global store (Realm docs are global metadata) via the provisioning
    // service. There is deliberately no `grant` subcommand: control-plane
    // authority is the ordinary realm:admin permission within whichever realm
    // holds the stored flag, so there is no permission to grant — only the
    // flag to move.

    private static async Task<int> ControlPlaneAsync(IServiceProvider services, string[] args)
    {
        var svc = services.GetRequiredService<IRealmProvisioningService>();
        var sub = (args.Length > 1 ? args[1] : "list").ToLowerInvariant();

        switch (sub)
        {
            case "list":
            {
                var cp = await svc.GetControlPlaneRealmAsync();
                if (cp is null)
                {
                    Console.WriteLine("No control-plane realm is currently set.");
                    return 0;
                }
                Console.WriteLine($"Control-plane realm: {cp.Slug}  ({cp.DisplayName})");
                Console.WriteLine($"  Domains: {string.Join(", ", cp.Domains)}");
                return 0;
            }
            case "transfer":
            {
                if (args.Length < 3)
                    return Error("Usage: recover control-plane transfer <slug>");
                var targetSlug = args[2].Trim().ToLowerInvariant();

                var result = await svc.TransferControlPlaneAsync(targetSlug);
                if (result.IsError)
                    return Error($"{result.FirstError.Code}: {result.FirstError.Description}");

                services.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
                {
                    EventType = AuditEvents.RecoveryCliInvoked,
                    Level = "Warning",
                    Realm = targetSlug,
                    Status = "succeeded",
                    Reason = $"control-plane transfer: Target={targetSlug}",
                    Message = $"Recovery control-plane transfer. Target={targetSlug}",
                });
                Console.WriteLine($"✓ Control plane transferred to realm '{targetSlug}'.");
                PrintRestartHint();
                return 0;
            }
            default:
                return Error($"Unknown control-plane subcommand: '{sub}'. Use 'list' or 'transfer <slug>'.");
        }
    }

    // ── adopt-tenant ──────────────────────────────────────────────────────
    //
    // Register an already-existing {master}_{slug} database as a realm without
    // CREATE DATABASE — the migration counterpart to creating a realm via the
    // API. The operator restores a dump into the target DB first, then runs
    // this to wire it into the tenant registry + global Realm store.

    private static async Task<int> AdoptTenantAsync(IServiceProvider services, string[] args)
    {
        // recover adopt-tenant <slug> <displayName> [domain]
        if (args.Length < 3)
            return Error("Usage: recover adopt-tenant <slug> <displayName> [domain]");

        var slug = args[1].Trim().ToLowerInvariant();
        var displayName = args[2];
        var domain = args.Length > 3 ? args[3] : null;

        var svc = services.GetRequiredService<IRealmProvisioningService>();
        var result = await svc.AdoptExistingDatabaseAsync(
            slug, displayName, domain is null ? null : [domain]);
        if (result.IsError)
            return Error($"{result.FirstError.Code}: {result.FirstError.Description}");

        services.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = slug,
            Status = "succeeded",
            Reason = $"adopt-tenant: Slug={slug}",
            Message = $"Recovery adopt-tenant. Slug={slug}",
        });
        Console.WriteLine($"✓ Adopted existing database as realm '{slug}'.");
        Console.WriteLine($"  Domains: {string.Join(", ", result.Value.Domains)}");
        PrintRestartHint();
        return 0;
    }

    /// <summary>
    /// Print a helpful restart hint after CLI mutations that the running
    /// server's in-process IRealmCache needs to pick up. The CLI runs in
    /// its own process via <c>docker exec dotnet ...</c>, so invalidating
    /// the CLI process's cache doesn't reach the server process. A
    /// container restart re-loads the cache on the next request.
    /// </summary>
    private static void PrintRestartHint()
    {
        Console.WriteLine();
        Console.WriteLine("⚠ Restart the running container so the in-process realm cache");
        Console.WriteLine("  picks up the change (the CLI runs as a separate process):");
        Console.WriteLine();
        Console.WriteLine("    docker compose restart auth   # or your Compose service name");
        Console.WriteLine();
    }

    // ── rotate-signing-key ──────────────────────────────────────────────

    private static async Task<int> RotateSigningKeyAsync(IServiceProvider services, string tenantId)
    {
        var keyStore = services.GetRequiredService<Modgud.Infrastructure.Realms.IRealmKeyStore>();

        Console.WriteLine($"Rotating signing key for realm '{tenantId}'...");
        var creds = await keyStore.RotateAsync(tenantId);
        var kid = creds.Key.KeyId;

        services.GetRequiredService<ISecurityAuditLog>().Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Level = "Warning",
            Realm = tenantId,
            Status = "rotated",
            Reason = $"rotate-signing-key: Realm={tenantId} NewKid={kid}",
            Message = $"Recovery rotate-signing-key. Realm={tenantId} NewKid={kid}",
        });
        Console.WriteLine($"  OK new active kid: {kid}");
        Console.WriteLine("  Previous key retired into the 30-day verification overlap window.");
        // The CLI is a separate process — it only mutates its OWN in-memory key
        // cache. A running API instance reconciles its cache against the DB within
        // RealmKeyStore.CacheRevalidateInterval (~60s), so no restart is needed;
        // the new key just isn't used for signing on live instances until then.
        Console.WriteLine("  Running API instances pick up the new key within ~60 seconds.");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}
