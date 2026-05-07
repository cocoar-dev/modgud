using System.Security.Cryptography;
using System.Text;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Setup;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Projections;
using Cocoar.Auth.Authorization.Services;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Domain.Users.Events;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Projections;


namespace Cocoar.Auth.Authentication.Api.Admin;

/// <summary>
/// Break-glass recovery CLI — for when the last admin locks themselves out of 2FA and
/// nobody else can help from the UI side. Run inside the running container:
///
///   docker exec &lt;container&gt; dotnet Cocoar.Auth.Api.dll recover list
///   docker exec &lt;container&gt; dotnet Cocoar.Auth.Api.dll recover reset-2fa &lt;username&gt;
///   docker exec &lt;container&gt; dotnet Cocoar.Auth.Api.dll recover magic-link &lt;username&gt;
///   docker exec &lt;container&gt; dotnet Cocoar.Auth.Api.dll recover help
///
/// Requires shell access to the host — anyone who can <c>docker exec</c> already has
/// DB access, so this doesn't open a new privilege-escalation path. Every invocation
/// is written to the standard auth log ("Auth:" prefix) with a <c>Recovery:</c>
/// subprefix so admins can audit usage after the fact.
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

        return command switch
        {
            "list" => await ListUsersAsync(session, permissions),
            "reset-2fa" => await Reset2FaAsync(session, userManager, args),
            "set-email" => await SetEmailAsync(session, userManager, args),
            "magic-link" => await MagicLinkAsync(session, scope.ServiceProvider, args, conf, env),
            "rebuild-projections" => await RebuildProjectionsAsync(scope.ServiceProvider),
            "bootstrap-admin" => await BootstrapAdminAsync(scope.ServiceProvider, args, realmSlug),
            "realm-add-domain" => await RealmAddDomainAsync(scope.ServiceProvider, args),
            "realm-remove-domain" => await RealmRemoveDomainAsync(scope.ServiceProvider, args),
            "realm-list" => await RealmListAsync(scope.ServiceProvider),
            _ => Error($"Unknown command: {command}. Try 'help'.")
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Cocoar.Auth Recovery CLI
            ─────────────────────

            Usage:
              dotnet Cocoar.Auth.Api.dll recover <command> [args...] [--realm <slug>]

            Commands:
              list                           List all users (UserName · Email · Active · 2FA · Passkeys).
              reset-2fa <username>           Disable TOTP + Email-OTP + delete all Passkeys for user.
              set-email <username> <email>   Update the user's email address (appends UserUpdatedEvent
                                             so projections + SignalR update live).
              magic-link <username>          Generate a one-time login URL and print it.
              rebuild-projections            Rebuild all Marten projections (inline + async).
                                             Bootstrap path for the first migration after
                                             a schema change when no admin can authenticate yet.
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
            // because realm:admin is universal — cocoar-auth picked here for
            // legibility only.
            var isAdmin = await permissions.HasPermissionAsync(user.Id, AppSlugs.CocoarAuth, PermissionEvaluator.RealmAdminPermission, CancellationToken.None);

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
        string[] args)
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

        Serilog.Log.Warning(
            "Auth: Recovery reset-2fa. User={UserName} TOTP={WasTotp} EmailOtp={WasEmailOtp} PasskeysDeleted={Passkeys}",
            user.UserName, wasTotpEnabled, wasEmailOtpEnabled, passkeys.Count);

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
        string[] args)
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
        var personConflict = await session.Query<Cocoar.Auth.Authorization.Principals.Person>()
            .Where(p => p.Email == newEmail && p.Id != user.Id && !p.IsDeleted)
            .AnyAsync();
        var groupConflict = await session.Query<Group>()
            .Where(g => g.Email == newEmail && !g.IsDeleted)
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

        Serilog.Log.Warning("Auth: Recovery set-email. User={UserName} Old={Old} New={New}",
            user.UserName, oldEmail, newEmail);

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

        Serilog.Log.Warning("Auth: Recovery magic-link generated. User={UserName} ExpiresAt={ExpiresAt}",
            user.UserName, challenge.ExpiresAt);

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
    private static async Task<int> RebuildProjectionsAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        var timeout = TimeSpan.FromMinutes(10);

        Console.WriteLine("Rebuilding Marten projections...");
        Serilog.Log.Warning("Auth: Recovery rebuild-projections initiated");

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.RebuildProjectionAsync("ViewProjections", timeout, CancellationToken.None);
        Console.WriteLine("  OK ViewProjections");

        await daemon.RebuildProjectionAsync<CocoarAuthPrincipalProjection>(timeout, CancellationToken.None);
        Console.WriteLine("  OK CocoarAuthPrincipalProjection (mt_doc_principal)");

        await daemon.RebuildProjectionAsync<PermissionRoleProjection>(timeout, CancellationToken.None);
        Console.WriteLine("  OK PermissionRoleProjection (mt_doc_permissionrole)");

        Serilog.Log.Warning("Auth: Recovery rebuild-projections completed");
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

        if (result.IsError)
        {
            Serilog.Log.Warning(
                "Auth: Recovery bootstrap-admin failed. Realm={Realm} UserName={UserName} Code={Code} Detail={Detail}",
                realmSlug, userName, result.FirstError.Code, result.FirstError.Description);
            return Error($"{result.FirstError.Code}: {result.FirstError.Description}");
        }

        var admin = result.Value;
        Serilog.Log.Warning(
            "Auth: Recovery bootstrap-admin succeeded. Realm={Realm} UserName={UserName} Mode=Direct",
            realmSlug, admin.UserName);

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

        Serilog.Log.Warning(
            "Auth: Recovery bootstrap-admin issued invite. Realm={Realm} UserName={UserName} Email={Email} ExpiresAt={ExpiresAt}",
            realmSlug, userName, email, invite.ExpiresAt);

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

    private static async Task<int> RealmListAsync(IServiceProvider services)
    {
        var globalStore = services.GetRequiredService<Cocoar.Auth.Infrastructure.Persistence.Tenancy.IGlobalStore>();
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

        var globalStore = services.GetRequiredService<Cocoar.Auth.Infrastructure.Persistence.Tenancy.IGlobalStore>();
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
        Serilog.Log.Warning("Auth: Recovery realm-add-domain — Realm={Slug} Domain={Domain}", slug, domain);
        return 0;
    }

    private static async Task<int> RealmRemoveDomainAsync(IServiceProvider services, string[] args)
    {
        var slug = ParseFlag(args, "--slug");
        var domain = ParseFlag(args, "--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return Error("realm-remove-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = services.GetRequiredService<Cocoar.Auth.Infrastructure.Persistence.Tenancy.IGlobalStore>();
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
        Serilog.Log.Warning("Auth: Recovery realm-remove-domain — Realm={Slug} Domain={Domain}", slug, domain);
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

    private static int Error(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}
