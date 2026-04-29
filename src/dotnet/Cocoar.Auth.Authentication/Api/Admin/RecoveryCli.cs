using System.Security.Cryptography;
using System.Text;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Projections;
using Cocoar.Auth.Authorization.Services;
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
            _ => Error($"Unknown command: {command}. Try 'help'.")
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Cocoar.Auth Recovery CLI
            ─────────────────────

            Usage:
              dotnet Cocoar.Auth.Api.dll recover <command> [args...]

            Commands:
              list                           List all users (UserName · Email · Active · 2FA · Passkeys).
              reset-2fa <username>           Disable TOTP + Email-OTP + delete all Passkeys for user.
              set-email <username> <email>   Update the user's email address (appends UserUpdatedEvent
                                             so projections + SignalR update live).
              magic-link <username>          Generate a one-time login URL and print it.
              rebuild-projections            Rebuild all Marten projections (inline + async).
                                             Bootstrap path for the first migration after
                                             a schema change when no admin can authenticate yet.
              help                           Show this message.

            All commands run against the configured database. No network access.
            Every invocation is written to the auth log.
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

    private static int Error(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}
