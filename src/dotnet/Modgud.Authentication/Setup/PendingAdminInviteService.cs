using System.Security.Cryptography;
using System.Text;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Persistence.Tenancy;
using ErrorOr;
using Marten;
using Microsoft.Extensions.Logging;

namespace Modgud.Authentication.Setup;

/// <summary>
/// Issues + consumes the one-shot bootstrap-invite for the first admin
/// in a realm (C15). Two issuance call-sites:
/// <list type="bullet">
///   <item><description>Recovery-CLI <c>bootstrap-admin</c> without
///   <c>--password</c></description></item>
///   <item><description>Realm-Provisioning's <c>InitialAdmin</c>
///   property at realm-creation time</description></item>
/// </list>
/// One consume call-site: <c>POST /api/account/bootstrap-admin</c>
/// (anonymous, rate-limited). On consume, the user is atomically created
/// + put into the Administrators group via <see cref="IRealmAdminBootstrapper"/>.
///
/// <para>Token format: 32 random bytes, Base64Url-encoded → URL-safe,
/// 43 chars. Stored as SHA-256 hex hash. Plain-text only ever lives in
/// the magic-link URL.</para>
/// </summary>
public interface IPendingAdminInviteService
{
    /// <summary>
    /// Issue a fresh invite. Stores a <see cref="PendingAdminInvite"/>
    /// in the current tenant DB (via the tenant-scoped
    /// <see cref="IDocumentSession"/>) and returns the magic-link URL
    /// to print/email. The plain-text token is only available here —
    /// after this method returns, only the SHA-256 hash is recoverable.
    ///
    /// <para>If a non-used, non-expired invite already exists for the
    /// same email in this realm, the old one is marked Used (revoked)
    /// before a new one is issued. This is the "resend" path.</para>
    /// </summary>
    Task<IssuedInvite> IssueAsync(
        string userName,
        string email,
        string? firstname,
        string? lastname,
        string? issuedBy,
        Realm realm,
        CancellationToken ct = default);

    /// <summary>
    /// Validate + consume the token. On success creates the admin user
    /// + roles + group via <see cref="IRealmAdminBootstrapper"/>, marks
    /// the invite UsedAt=now, and returns the bootstrapped admin so the
    /// endpoint can sign them in. On failure returns an
    /// <see cref="Error"/> describing why (expired / used / unknown
    /// token / weak password).
    ///
    /// <para>Consistency (Audit #31): the user-create commits first (through the
    /// UserManager's own store session), then the role/group seed, then the invite
    /// is marked used — these are SEPARATE commits, not one transaction. A weak
    /// password is rejected before any commit, so the invite stays unused and can be
    /// retried. A crash after the admin exists but before the invite is marked used
    /// leaves a benign INERT token: the admin already exists, so the unique
    /// username/email indexes block a second admin, and a later replay is detected
    /// (the target admin exists) and the invite marked used then.</para>
    /// </summary>
    Task<ErrorOr<BootstrappedAdmin>> ConsumeAsync(
        string plaintextToken,
        string password,
        CancellationToken ct = default);
}

public sealed record IssuedInvite(
    Guid InviteId,
    string PlaintextToken,
    string MagicLinkUrl,
    DateTimeOffset ExpiresAt,
    string Email,
    string UserName);

public sealed class PendingAdminInviteService(
    IDocumentSession session,
    IRealmAdminBootstrapper bootstrapper,
    IEmailService emailService,
    IWebHostEnvironment env,
    ISecurityAuditLog securityAudit,
    ILogger<PendingAdminInviteService> logger) : IPendingAdminInviteService
{
    public async Task<IssuedInvite> IssueAsync(
        string userName,
        string email,
        string? firstname,
        string? lastname,
        string? issuedBy,
        Realm realm,
        CancellationToken ct = default)
    {
        var normalizedUserName = userName.Trim().ToLowerInvariant();
        var normalizedEmail = email.Trim();

        // Revoke any open invites for the same email in this realm.
        // This makes IssueAsync the resend path too: a new call invalidates
        // the previous link. (Tenant-scoped session so this only reaches
        // invites in the current realm DB.)
        var openInvites = await session.Query<PendingAdminInvite>()
            .Where(i => i.Email == normalizedEmail && i.UsedAt == null)
            .ToListAsync(ct);
        foreach (var open in openInvites)
        {
            open.UsedAt = DateTimeOffset.UtcNow;
            session.Store(open);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var invite = new PendingAdminInvite
        {
            Id = Guid.NewGuid(),
            UserName = normalizedUserName,
            Email = normalizedEmail,
            Firstname = firstname,
            Lastname = lastname,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(PendingAdminInvite.DefaultExpirationDays),
            CreatedAt = DateTimeOffset.UtcNow,
            IssuedBy = issuedBy,
        };
        session.Store(invite);
        await session.SaveChangesAsync(ct);

        var url = BuildMagicLinkUrl(realm, token);

        // Send email. In Dev with InMemoryEmailService the email lands in
        // memory and the link is also written by the caller (CLI / endpoint)
        // to stdout / response. Failure to send is logged but doesn't fail
        // the issue — the URL is still in the IssuedInvite return value.
        try
        {
            var displayName = !string.IsNullOrWhiteSpace(firstname)
                ? $"{firstname} {lastname}".Trim()
                : normalizedUserName;
            await emailService.SendTemplatedEmailAsync(
                normalizedEmail,
                EmailTemplate.RealmAdminBootstrap,
                new Dictionary<string, string>
                {
                    ["AppName"] = realm.DisplayName,
                    ["DisplayName"] = displayName,
                    ["UserName"] = normalizedUserName,
                    ["Email"] = normalizedEmail,
                    ["RealmDisplayName"] = realm.DisplayName,
                    ["ActionUrl"] = url,
                    ["ExpirationDays"] = PendingAdminInvite.DefaultExpirationDays.ToString(),
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Bootstrap-invite issued but email delivery failed. Realm={Realm} Email={MaskedEmail}. The plaintext URL is still on the issuer's side.",
                realm.Slug, LogPiiMasking.MaskEmail(normalizedEmail));
        }

        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.BootstrapInviteIssued,
            Level = "Info",
            Actor = LogPiiMasking.MaskEmail(normalizedEmail),
            Status = "issued",
            Reason = $"expires {invite.ExpiresAt}, issued by {issuedBy ?? "(self/CLI)"}",
            Message = "Bootstrap invite issued",
        });

        return new IssuedInvite(invite.Id, token, url, invite.ExpiresAt, normalizedEmail, normalizedUserName);
    }

    public async Task<ErrorOr<BootstrappedAdmin>> ConsumeAsync(
        string plaintextToken,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            return Error.Validation("BootstrapInvite.TokenRequired", "Token is required.");

        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));

        var invite = await session.Query<PendingAdminInvite>()
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

        if (invite is null)
            return Error.Validation("BootstrapInvite.TokenUnknown", "Invite is invalid.");
        if (invite.IsUsed)
            return Error.Validation("BootstrapInvite.TokenUsed", "Invite has already been used.");
        if (invite.IsExpired)
            return Error.Validation("BootstrapInvite.TokenExpired", "Invite has expired.");

        var bootstrapResult = await bootstrapper.BootstrapDirectAsync(
            invite.UserName, password, invite.Email,
            invite.Firstname, invite.Lastname, ct);

        if (bootstrapResult.IsError)
        {
            // Audit #31 — distinguish a retryable failure from a spent invite.
            // A weak password is rejected before the user is created, so keep the
            // invite armed for a retry. But if the target admin ALREADY exists, a
            // prior consume succeeded (possibly crashing before it marked the invite
            // used) — the invite is spent, so mark it used now instead of leaving it
            // armed indefinitely. (Replays can't create a second admin anyway: the
            // unique username/email indexes reject them — this just stops the stale
            // armed token + the misleading audit trail.)
            var adminAlreadyExists = await session.Query<ApplicationUser>()
                .AnyAsync(u => !u.IsDeleted && u.NormalizedEmail == invite.Email.ToUpperInvariant(), ct);
            if (adminAlreadyExists)
            {
                invite.UsedAt = DateTimeOffset.UtcNow;
                session.Store(invite);
                await session.SaveChangesAsync(ct);
            }
            return bootstrapResult.Errors;
        }

        invite.UsedAt = DateTimeOffset.UtcNow;
        session.Store(invite);
        await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "Bootstrap-invite consumed. UserId={UserId} Email={MaskedEmail}",
            bootstrapResult.Value.UserId, LogPiiMasking.MaskEmail(invite.Email));

        return bootstrapResult.Value;
    }

    private string BuildMagicLinkUrl(Realm realm, string plaintextToken)
    {
        // The link is built against the realm's canonical public host
        // (PrimaryDomain) — the same origin used for every other outbound
        // link in the realm. In Dev that's http://{host}:4300 (the SPA dev
        // server, where /bootstrap is served), in Prod https://{host}.
        var baseUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
        return $"{baseUrl}/bootstrap?token={Uri.EscapeDataString(plaintextToken)}";
    }
}
