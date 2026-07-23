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
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Domain;
using Modgud.Domain.Users.Events;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Projections;

namespace Modgud.Authentication.Api.Admin;

// One class per Recovery-CLI command. The dispatcher in RecoveryCli resolves the
// tenant, enters the TenantContext, opens a DI scope, and calls ExecuteAsync; each
// command resolves only the services it needs from ctx.Services and writes through
// ctx.Out / ctx.Error. The execution model is unchanged from the original
// monolith (same in-process host boot, same real events) — this is internal
// modularization only.

// ── list ────────────────────────────────────────────────────────────────

internal sealed class ListCommand : IRecoveryCommand
{
    public string Name => "list";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var session = ctx.Services.GetRequiredService<IDocumentSession>();
        var permissions = ctx.Services.GetRequiredService<IPermissionService>();

        var users = await session.Query<ApplicationUser>()
            .Where(u => !u.IsDeleted)
            .OrderBy(u => u.UserName)
            .ToListAsync();

        ctx.WriteLine($"{"UserName",-20} {"Email",-40} {"Active",-8} {"Admin",-7} {"2FA",-6} {"Passkeys"}");
        ctx.WriteLine(new string('─', 100));

        foreach (var user in users)
        {
            var passkeyCount = await session.Query<StoredPasskeyCredential>()
                .Where(p => p.UserId == user.Id)
                .CountAsync();

            var mfa = user.TwoFactorEnabled ? "TOTP" : (user.EmailOtpEnabled ? "EMAIL" : "-");
            // "Admin" = realm-wide admin: the user holds realm:admin (typically via
            // the System Admin role + Administrators group). Checked against any app
            // slug because realm:admin is universal — modgud picked for legibility.
            var isAdmin = await permissions.HasPermissionAsync(user.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, CancellationToken.None);

            ctx.WriteLine(
                $"{user.UserName,-20} {user.Email ?? "",-40} {(user.IsActive ? "yes" : "no"),-8} " +
                $"{(isAdmin ? "yes" : "no"),-7} {mfa,-6} {passkeyCount}");
        }

        return 0;
    }
}

// ── reset-2fa ─────────────────────────────────────────────────────────────

internal sealed class Reset2FaCommand : IRecoveryCommand
{
    public string Name => "reset-2fa";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        if (ctx.Args.Length < 2) return ctx.Fail("Usage: recover reset-2fa <username>");
        var userName = ctx.Args[1].Trim().ToLowerInvariant();

        var session = ctx.Services.GetRequiredService<IDocumentSession>();
        var userManager = ctx.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var securityAudit = ctx.Services.GetRequiredService<ISecurityAuditLog>();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return ctx.Fail($"User not found: {userName}");

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

        await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            TargetSubjectId = user.Id,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "reset-2fa",
            ReasonCode = $"totp:{wasTotpEnabled};email-otp:{wasEmailOtpEnabled}",
            Count = passkeys.Count,
        });

        ctx.WriteLine($"✓ 2FA reset for {user.UserName}:");
        ctx.WriteLine($"  TOTP disabled:    {(wasTotpEnabled ? "yes" : "was already off")}");
        ctx.WriteLine($"  Email-OTP off:    {(wasEmailOtpEnabled ? "yes" : "was already off")}");
        ctx.WriteLine($"  Passkeys deleted: {passkeys.Count}");
        ctx.WriteLine($"  Grace period:    reset (fresh window on next login)");
        return 0;
    }
}

// ── set-email ─────────────────────────────────────────────────────────────

internal sealed class SetEmailCommand : IRecoveryCommand
{
    public string Name => "set-email";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        if (ctx.Args.Length < 3) return ctx.Fail("Usage: recover set-email <username> <new-email>");
        var userName = ctx.Args[1].Trim().ToLowerInvariant();
        var newEmail = ctx.Args[2].Trim();

        if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains('@'))
            return ctx.Fail($"Invalid email address: {newEmail}");

        var session = ctx.Services.GetRequiredService<IDocumentSession>();
        var userManager = ctx.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var securityAudit = ctx.Services.GetRequiredService<ISecurityAuditLog>();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return ctx.Fail($"User not found: {userName}");

        // Uniqueness guard — same check UpdateUserCommand runs. The polymorphic
        // Principal projection is inline, so this is strongly consistent.
        var normalizedEmail = newEmail.ToUpperInvariant();
        var personConflict = await session.Query<Person>()
            .Where(p => p.NormalizedEmail == normalizedEmail && p.Id != user.Id && !p.IsDeleted)
            .AnyAsync();
        var groupConflict = await session.Query<Group>()
            .Where(g => g.Email != null && g.Email.ToUpper() == normalizedEmail && !g.IsDeleted)
            .AnyAsync();
        if (personConflict || groupConflict)
            return ctx.Fail($"Email already in use by another principal: {newEmail}");

        var oldEmail = user.Email;

        // Update the Identity document + append the domain event in the same
        // transaction, so the UserView projection + label sync + SignalR dispatch
        // catch up without a race window.
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

        await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            TargetSubjectId = user.Id,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "set-email",
        });

        ctx.WriteLine($"✓ Email updated for {user.UserName}:");
        ctx.WriteLine($"  Old: {oldEmail ?? "(none)"}");
        ctx.WriteLine($"  New: {newEmail}");
        return 0;
    }
}

// ── magic-link ────────────────────────────────────────────────────────────

internal sealed class MagicLinkCommand : IRecoveryCommand
{
    public string Name => "magic-link";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        if (ctx.Args.Length < 2) return ctx.Fail("Usage: recover magic-link <username>");
        var userName = ctx.Args[1].Trim().ToLowerInvariant();

        var session = ctx.Services.GetRequiredService<IDocumentSession>();
        var userManager = ctx.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(userName);
        if (user is null) return ctx.Fail($"User not found: {userName}");
        if (!user.IsActive) return ctx.Fail($"User is inactive: {userName}");

        // The link host is the realm's canonical public domain — the Realm doc
        // lives in the global store; ctx.RealmSlug (default "system") names which
        // realm we're acting in.
        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var globalSession = globalStore.QuerySession();
        var realm = await globalSession.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == ctx.RealmSlug);
        if (realm is null) return ctx.Fail($"Realm '{ctx.RealmSlug}' not found.");

        // Clear old challenges + create a fresh one (same pattern as AdminMagicLinkEndpoints).
        var existing = await session.Query<MagicLinkChallenge>()
            .Where(c => c.UserId == user.Id)
            .ToListAsync();
        foreach (var old in existing)
            session.Delete(old);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var expirationMinutes = ctx.Services.GetService<IMagicLinkConfiguration>()?.ExpirationMinutes ?? 15;

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

        var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, ctx.Env);
        var url = $"{appUrl}/magic-login?userId={user.Id}&token={Uri.EscapeDataString(token)}";

        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            TargetSubjectId = user.Id,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "generate-magic-link",
            EffectiveAt = challenge.ExpiresAt,
        });

        ctx.WriteLine($"✓ Magic link for {user.UserName} (expires in {expirationMinutes} min):");
        ctx.WriteLine();
        ctx.WriteLine($"  {url}");
        ctx.WriteLine();
        ctx.WriteLine("Open in a browser — single use, 2FA bypassed.");
        return 0;
    }
}

// ── rebuild-projections ───────────────────────────────────────────────────

/// <summary>
/// Rebuilds all Marten projections from event 0. Mirrors the admin rebuild
/// endpoint but runs without auth — needed when a schema change leaves
/// <c>mt_doc_principal</c> empty so no user can claim <c>app:admin</c> until the
/// principal projection is replayed.
/// </summary>
internal sealed class RebuildProjectionsCommand : IRecoveryCommand
{
    public string Name => "rebuild-projections";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var store = ctx.Services.GetRequiredService<IDocumentStore>();
        var timeout = TimeSpan.FromMinutes(10);

        ctx.WriteLine("Rebuilding Marten projections...");
        var securityAudit = ctx.Services.GetRequiredService<ISecurityAuditLog>();
        await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            OutcomeCode = AuditOutcomes.Initiated,
            OperationCode = "rebuild-projections",
        });

        // MasterTableTenancy disables Marten's default tenant, so the no-arg
        // overload throws DefaultTenantUsageDisabledException — build the daemon
        // for the resolved realm's DB explicitly (honors --realm; default system).
        using var daemon = await store.BuildProjectionDaemonAsync(ctx.RealmSlug);
        await daemon.RebuildProjectionAsync("ViewProjections", timeout, CancellationToken.None);
        ctx.WriteLine("  OK ViewProjections");

        await daemon.RebuildProjectionAsync<ModgudPrincipalProjection>(timeout, CancellationToken.None);
        ctx.WriteLine("  OK ModgudPrincipalProjection (mt_doc_principal)");

        await daemon.RebuildProjectionAsync<PermissionRoleProjection>(timeout, CancellationToken.None);
        ctx.WriteLine("  OK PermissionRoleProjection (mt_doc_permissionrole)");

        await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "rebuild-projections",
        });
        return 0;
    }
}

// ── bootstrap-admin ───────────────────────────────────────────────────────

/// <summary>
/// First-admin creation for a realm. Direct mode (<c>--password</c>) seeds an
/// atomic user + role + group via <see cref="IRealmAdminBootstrapper"/> with
/// Identity password rules enforced; invite mode (no <c>--password</c>) writes a
/// <see cref="PendingAdminInvite"/> and prints a magic link.
/// </summary>
internal sealed class BootstrapAdminCommand : IRecoveryCommand
{
    public string Name => "bootstrap-admin";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var email = ctx.Flag("--email");
        if (string.IsNullOrWhiteSpace(email))
            return ctx.Fail("Usage: recover bootstrap-admin --email <email> [--username <name>] [--firstname <name>] [--lastname <name>] [--password <pw>] [--realm <slug>]");

        var userName = ctx.Flag("--username")?.Trim().ToLowerInvariant()
            ?? email.Split('@', 2)[0].ToLowerInvariant();
        var firstname = ctx.Flag("--firstname");
        var lastname = ctx.Flag("--lastname");
        var password = ctx.Flag("--password");

        if (string.IsNullOrEmpty(password))
        {
            // Invite mode: write a PendingAdminInvite + send email + print the
            // magic-link URL on stdout.
            return await IssueInviteAsync(ctx, userName, email, firstname, lastname);
        }

        var bootstrapper = ctx.Services.GetRequiredService<IRealmAdminBootstrapper>();
        var result = await bootstrapper.BootstrapDirectAsync(userName, password, email, firstname, lastname);

        var securityAudit = ctx.Services.GetRequiredService<ISecurityAuditLog>();
        if (result.IsError)
        {
            await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
            {
                EventType = AuditEvents.RecoveryCliInvoked,
                Severity = AuditSeverity.Warning,
                RealmSlug = ctx.RealmSlug,
                ActorKind = AuditActorKind.System,
                UnknownIdentifier = userName,
                OutcomeCode = AuditOutcomes.Failed,
                OperationCode = "bootstrap-admin-direct",
                ReasonCode = result.FirstError.Code,
            });
            return ctx.Fail($"{result.FirstError.Code}: {result.FirstError.Description}");
        }

        var admin = result.Value;
        await securityAudit.RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            TargetSubjectId = admin.UserId,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "bootstrap-admin-direct",
        });

        ctx.WriteLine($"✓ Admin created in realm '{ctx.RealmSlug}':");
        ctx.WriteLine($"  UserName: {admin.UserName}");
        ctx.WriteLine($"  Email:    {admin.Email}");
        ctx.WriteLine($"  Mode:     Direct (password set on creation)");
        ctx.WriteLine();
        ctx.WriteLine("Sign in via the realm's domain — the user is in the Administrators group with realm:admin.");
        return 0;
    }

    private static async Task<int> IssueInviteAsync(
        RecoveryCliContext ctx, string userName, string email, string? firstname, string? lastname)
    {
        // Look up the realm in the global store — the invite service needs the
        // DisplayName + Domains[] for the email template + magic-link URL.
        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var globalSession = globalStore.QuerySession();
        var realm = await globalSession.Query<Realm>()
            .FirstOrDefaultAsync(r => r.Slug == ctx.RealmSlug);
        if (realm is null)
            return ctx.Fail($"Realm '{ctx.RealmSlug}' not found.");

        var inviteService = ctx.Services.GetRequiredService<IPendingAdminInviteService>();
        var invite = await inviteService.IssueAsync(
            userName, email, firstname, lastname,
            issuedBy: null, // CLI invocation — no authenticated CP-admin
            realm);

        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            UnknownIdentifier = email,
            OutcomeCode = AuditOutcomes.Initiated,
            OperationCode = "bootstrap-admin-invite",
            EffectiveAt = invite.ExpiresAt,
        });

        ctx.WriteLine($"✓ Bootstrap-invite issued for realm '{ctx.RealmSlug}':");
        ctx.WriteLine($"  UserName:  {invite.UserName}");
        ctx.WriteLine($"  Email:     {invite.Email}");
        ctx.WriteLine($"  Expires:   {invite.ExpiresAt:yyyy-MM-dd HH:mm:ss zzz}");
        ctx.WriteLine();
        ctx.WriteLine($"  Link:      {invite.MagicLinkUrl}");
        ctx.WriteLine();
        ctx.WriteLine("Recipient opens the link, sets a password, signs in. The link is single-use.");
        return 0;
    }
}

// ── migrate-cc-credentials ────────────────────────────────────────────────

/// <summary>
/// Phase-2C retrofit: for every <c>client_credentials</c>-grant OAuth client
/// without a LinkedServiceAccountId, auto-provision a <c>legacy.{clientId}</c>
/// ServiceAccount and backfill the link so the SA-managed mutation guard applies.
/// Idempotent.
/// </summary>
internal sealed class MigrateCcCredentialsCommand : IRecoveryCommand
{
    public string Name => "migrate-cc-credentials";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var session = ctx.Services.GetRequiredService<IDocumentSession>();

        var unlinked = await session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted
                     && x.LinkedServiceAccountId == null
                     && x.Permissions.Contains(OAuthPermissions.GrantTypes.ClientCredentials))
            .ToListAsync();

        if (unlinked.Count == 0)
        {
            ctx.WriteLine($"✓ No client_credentials clients need migration in realm '{ctx.RealmSlug}'.");
            return 0;
        }

        ctx.WriteLine($"Found {unlinked.Count} unlinked client_credentials client(s) in realm '{ctx.RealmSlug}'.");

        var migrated = 0;
        var saReused = 0;
        var saCreated = 0;

        foreach (var client in unlinked)
        {
            var sanitized = SanitizeForAccountName(client.ClientId);
            var saName = $"legacy.{sanitized}";

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

            var aggregate = await session.Events
                .AggregateStreamAsync<OAuthApplicationAggregate>(client.Id);
            if (aggregate is null || aggregate.IsDeleted)
                continue;

            session.Events.Append(client.Id, aggregate.SetLinkedServiceAccountId(sa.Id));
            migrated++;

            ctx.WriteLine($"  → {client.ClientId}  →  SA '{saName}'");
        }

        await session.SaveChangesAsync();

        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "migrate-cc-credentials",
            Count = migrated,
            RelatedCount = saCreated,
            ReusedCount = saReused,
        });

        ctx.WriteLine();
        ctx.WriteLine($"✓ Done. Migrated={migrated}  ServiceAccounts created={saCreated}  re-used={saReused}");
        return 0;
    }

    /// <summary>
    /// Coerce an OAuth client_id into the SA AccountName charset
    /// (<c>^[a-z0-9][a-z0-9._-]{1,63}$</c>). Lowercase + replace anything outside
    /// the allowed set with a hyphen. Truncates to 56 chars so the resulting
    /// <c>legacy.{...}</c> still fits the 64-char total budget.
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
}

// ── realm-list ────────────────────────────────────────────────────────────

internal sealed class RealmListCommand : IRecoveryCommand
{
    public string Name => "realm-list";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realms = await session.Query<Realm>()
            .Where(r => r.IsActive)
            .OrderBy(r => r.Slug)
            .ToListAsync();

        ctx.WriteLine($"{"Slug",-20} {"DisplayName",-30} {"PrimaryDomain",-25} {"Domains"}");
        ctx.WriteLine(new string('─', 115));
        foreach (var r in realms)
        {
            var cpMarker = r.IsControlPlane ? " [CP]" : "";
            ctx.WriteLine($"{r.Slug + cpMarker,-20} {r.DisplayName,-30} {r.PrimaryDomain,-25} {string.Join(", ", r.Domains)}");
        }
        return 0;
    }
}

// ── realm-add-domain ──────────────────────────────────────────────────────

internal sealed class RealmAddDomainCommand : IRecoveryCommand
{
    public string Name => "realm-add-domain";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var slug = ctx.Flag("--slug");
        var domain = ctx.Flag("--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return ctx.Fail("realm-add-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug);
        if (realm is null) return ctx.Fail($"Realm '{slug}' not found.");
        if (!realm.IsActive) return ctx.Fail($"Realm '{slug}' is not active.");

        if (realm.Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.WriteLine($"Realm '{slug}' already has domain '{domain}'. No change.");
            return 0;
        }

        realm.Domains = [.. realm.Domains, domain];
        realm.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(realm);
        await session.SaveChangesAsync();

        ctx.WriteLine($"✓ Added '{domain}' to realm '{slug}'. Now: [{string.Join(", ", realm.Domains)}]");
        ctx.PrintRestartHint();
        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "realm-add-domain",
            Domain = domain,
        });
        return 0;
    }
}

// ── realm-remove-domain ───────────────────────────────────────────────────

internal sealed class RealmRemoveDomainCommand : IRecoveryCommand
{
    public string Name => "realm-remove-domain";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var slug = ctx.Flag("--slug");
        var domain = ctx.Flag("--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return ctx.Fail("realm-remove-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug);
        if (realm is null) return ctx.Fail($"Realm '{slug}' not found.");

        // Guard: never strip a realm down to zero domains, and never remove the
        // canonical PrimaryDomain out from under the outbound-link + WebAuthn-RP
        // invariant — re-point the primary (realm-set-primary-domain) first.
        if (string.Equals(realm.PrimaryDomain, domain, StringComparison.OrdinalIgnoreCase))
            return ctx.Fail($"Cannot remove '{domain}' — it is the realm's PrimaryDomain. Set a different primary first (realm-set-primary-domain).");

        var remaining = realm.Domains
            .Where(d => !string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == realm.Domains.Length)
        {
            ctx.WriteLine($"Realm '{slug}' did not have domain '{domain}'. No change.");
            return 0;
        }
        if (remaining.Length == 0)
            return ctx.Fail($"Cannot remove '{domain}' — it is the realm's last domain. A realm must keep at least one.");

        realm.Domains = remaining;
        realm.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(realm);
        await session.SaveChangesAsync();

        ctx.WriteLine($"✓ Removed '{domain}' from realm '{slug}'. Now: [{string.Join(", ", remaining)}]");
        ctx.PrintRestartHint();
        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "realm-remove-domain",
            Domain = domain,
        });
        return 0;
    }
}

// ── realm-set-primary-domain ──────────────────────────────────────────────

/// <summary>
/// Re-point a realm's canonical public host. The new primary must already be in
/// the realm's Domains. The PrimaryDomain is the WebAuthn RP ID, so changing it
/// invalidates every existing passkey in the realm.
/// </summary>
internal sealed class RealmSetPrimaryDomainCommand : IRecoveryCommand
{
    public string Name => "realm-set-primary-domain";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var slug = ctx.Flag("--slug");
        var domain = ctx.Flag("--domain");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain))
            return ctx.Fail("realm-set-primary-domain requires --slug <slug> and --domain <hostname>.");

        var globalStore = ctx.Services.GetRequiredService<IGlobalStore>();
        await using var session = globalStore.LightweightSession();
        var realm = await session.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug);
        if (realm is null) return ctx.Fail($"Realm '{slug}' not found.");

        // The primary must be one of the realm's domains — name it via
        // realm-add-domain first if it isn't there yet.
        if (!realm.Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            return ctx.Fail($"Domain '{domain}' is not in realm '{slug}' (Domains: [{string.Join(", ", realm.Domains)}]). Add it first with realm-add-domain.");

        if (string.Equals(realm.PrimaryDomain, domain, StringComparison.OrdinalIgnoreCase))
        {
            ctx.WriteLine($"Realm '{slug}' already has PrimaryDomain '{domain}'. No change.");
            return 0;
        }

        var oldPrimary = realm.PrimaryDomain;
        realm.PrimaryDomain = domain;
        realm.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(realm);
        await session.SaveChangesAsync();

        ctx.WriteLine($"✓ Set PrimaryDomain for realm '{slug}': '{oldPrimary}' → '{domain}'.");
        ctx.WriteLine();
        ctx.WriteLine("⚠ The PrimaryDomain is the WebAuthn relying-party ID. Changing it");
        ctx.WriteLine("  INVALIDATES every existing passkey registered for this realm —");
        ctx.WriteLine("  affected users must re-register their passkeys (other login");
        ctx.WriteLine("  methods are unaffected).");
        ctx.PrintRestartHint();
        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "realm-set-primary-domain",
            Domain = domain,
            PreviousDomain = oldPrimary,
        });
        return 0;
    }
}

// ── control-plane ─────────────────────────────────────────────────────────

/// <summary>
/// Inspect or relocate the control-plane role. Operates on the global store via
/// the provisioning service. No <c>grant</c> subcommand — control-plane authority
/// is the ordinary realm:admin permission within whichever realm holds the flag.
/// </summary>
internal sealed class ControlPlaneCommand : IRecoveryCommand
{
    public string Name => "control-plane";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var svc = ctx.Services.GetRequiredService<IRealmProvisioningService>();
        var sub = (ctx.Args.Length > 1 ? ctx.Args[1] : "list").ToLowerInvariant();

        switch (sub)
        {
            case "list":
            {
                var cp = await svc.GetControlPlaneRealmAsync();
                if (cp is null)
                {
                    ctx.WriteLine("No control-plane realm is currently set.");
                    return 0;
                }
                ctx.WriteLine($"Control-plane realm: {cp.Slug}  ({cp.DisplayName})");
                ctx.WriteLine($"  Domains: {string.Join(", ", cp.Domains)}");
                return 0;
            }
            case "transfer":
            {
                if (ctx.Args.Length < 3)
                    return ctx.Fail("Usage: recover control-plane transfer <slug>");
                var targetSlug = ctx.Args[2].Trim().ToLowerInvariant();

                var result = await svc.TransferControlPlaneAsync(targetSlug);
                if (result.IsError)
                    return ctx.Fail($"{result.FirstError.Code}: {result.FirstError.Description}");

                await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordPlatformRequiredAsync(new PlatformAuditRecord
                {
                    EventType = AuditEvents.RecoveryCliInvoked,
                    Severity = AuditSeverity.Warning,
                    TargetRealmSlug = targetSlug,
                    OutcomeCode = AuditOutcomes.Succeeded,
                    OperationCode = "control-plane-transfer",
                });
                ctx.WriteLine($"✓ Control plane transferred to realm '{targetSlug}'.");
                ctx.PrintRestartHint();
                return 0;
            }
            default:
                return ctx.Fail($"Unknown control-plane subcommand: '{sub}'. Use 'list' or 'transfer <slug>'.");
        }
    }
}

// ── adopt-tenant ──────────────────────────────────────────────────────────

/// <summary>
/// Register an already-existing <c>{master}_{slug}</c> database as a realm
/// without CREATE DATABASE — the migration counterpart to creating a realm via
/// the API.
/// </summary>
internal sealed class AdoptTenantCommand : IRecoveryCommand
{
    public string Name => "adopt-tenant";
    public bool RequiresRealm => false;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        // recover adopt-tenant <slug> <displayName> [domain]
        if (ctx.Args.Length < 3)
            return ctx.Fail("Usage: recover adopt-tenant <slug> <displayName> [domain]");

        var slug = ctx.Args[1].Trim().ToLowerInvariant();
        var displayName = ctx.Args[2];
        var domain = ctx.Args.Length > 3 ? ctx.Args[3] : null;

        var svc = ctx.Services.GetRequiredService<IRealmProvisioningService>();
        var result = await svc.AdoptExistingDatabaseAsync(
            slug, displayName, domain is null ? null : [domain]);
        if (result.IsError)
            return ctx.Fail($"{result.FirstError.Code}: {result.FirstError.Description}");

        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordPlatformRequiredAsync(new PlatformAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            TargetRealmSlug = slug,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "adopt-tenant",
        });
        ctx.WriteLine($"✓ Adopted existing database as realm '{slug}'.");
        ctx.WriteLine($"  Domains: {string.Join(", ", result.Value.Domains)}");
        ctx.PrintRestartHint();
        return 0;
    }
}

// ── rotate-signing-key ────────────────────────────────────────────────────

internal sealed class RotateSigningKeyCommand : IRecoveryCommand
{
    public string Name => "rotate-signing-key";
    public bool RequiresRealm => true;

    public async Task<int> ExecuteAsync(RecoveryCliContext ctx)
    {
        var keyStore = ctx.Services.GetRequiredService<IRealmKeyStore>();

        ctx.WriteLine($"Rotating signing key for realm '{ctx.RealmSlug}'...");
        var creds = await keyStore.RotateAsync(ctx.RealmSlug);
        var kid = creds.Key.KeyId;

        await ctx.Services.GetRequiredService<ISecurityAuditLog>().RecordRequiredAsync(new SecurityAuditRecord
        {
            EventType = AuditEvents.RecoveryCliInvoked,
            Severity = AuditSeverity.Warning,
            RealmSlug = ctx.RealmSlug,
            ActorKind = AuditActorKind.System,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "rotate-signing-key",
            KeyId = kid,
        });
        ctx.WriteLine($"  OK new active kid: {kid}");
        ctx.WriteLine("  Previous key retired into the 30-day verification overlap window.");
        ctx.WriteLine("  Running API instances pick up the new key within ~60 seconds.");
        return 0;
    }
}
