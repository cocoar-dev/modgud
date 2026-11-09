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
/// Issues + consumes a one-shot realm-admin invitation (C15). Issuance
/// is available from the Control-Plane API and the recovery CLI:
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
    /// <para>Every non-used invite in the realm is marked Used (revoked)
    /// before a new one is issued, so only one admin invitation can be
    /// active per realm.</para>
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
    /// <para>Consistency (Audit #31, IS-INV-01): the invite is claimed first
    /// (version-checked), then the user-create commits (through the UserManager's own
    /// store session), then the role/group seed — SEPARATE commits, not one
    /// transaction. A weak password is rejected before the user exists, so the claim
    /// is re-armed and the invite can be retried (unless a newer invite was issued
    /// meanwhile). A crash after the claim leaves the invite used; the operator issues
    /// a new one.</para>
    /// </summary>
    Task<ErrorOr<BootstrappedAdmin>> ConsumeAsync(
        string plaintextToken,
        string password,
        CancellationToken ct = default);
}

/// <summary>IS-INV-01 — another issue or consume of this realm's admin invite committed
/// between the read and the write; nothing was stored. Issue again.</summary>
public sealed class AdminInviteConflictException(Exception inner)
    : InvalidOperationException(
        "Another admin invite for this realm was issued or consumed at the same moment; issue it again.", inner);

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

        // A realm may have exactly one open admin invite. Issuing a new one
        // revokes every prior open invite, regardless of recipient.
        var openInvites = await session.Query<PendingAdminInvite>()
            .Where(i => i.UsedAt == null)
            .ToListAsync(ct);
        foreach (var open in openInvites)
        {
            open.UsedAt = DateTimeOffset.UtcNow;
            open.OpenSlot = null;
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
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(PendingAdminInvite.DefaultExpirationHours),
            CreatedAt = DateTimeOffset.UtcNow,
            IssuedBy = issuedBy,
            OpenSlot = PendingAdminInvite.OpenSlotValue,
        };
        session.Store(invite);
        securityAudit.StoreRequired(session, new SecurityAuditRecord
        {
            EventType = AuditEvents.BootstrapInviteIssued,
            CaptureRequestContext = false,
            ActorKind = issuedBy is null
                ? AuditActorKind.System
                : AuditActorKind.ControlPlane,
            OutcomeCode = AuditOutcomes.Succeeded,
            OperationCode = "issue",
            EffectiveAt = invite.ExpiresAt,
        });
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsLostRace(ex))
        {
            // IS-INV-01 — another issue or a consume changed the open invite since it
            // was read; nothing was stored, the caller issues again.
            throw new AdminInviteConflictException(ex);
        }

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
                    ["ExpirationHours"] = PendingAdminInvite.DefaultExpirationHours.ToString(),
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Bootstrap-invite issued but email delivery failed. Realm={Realm} Email={MaskedEmail}. The plaintext URL is still on the issuer's side.",
                realm.Slug, LogPiiMasking.MaskEmail(normalizedEmail));
        }

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

        // IS-INV-01 — claim the invite before the admin is created, version-checked: a
        // reissue that revoked it after it was loaded wins, and a reissue after the claim
        // no longer sees it as open.
        invite.UsedAt = DateTimeOffset.UtcNow;
        invite.OpenSlot = null;
        session.Store(invite);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            return Error.Validation("BootstrapInvite.TokenUsed", "Invite has already been used.");
        }

        var bootstrapResult = await bootstrapper.BootstrapDirectAsync(
            invite.UserName, password, invite.Email,
            invite.Firstname, invite.Lastname, ct);

        if (bootstrapResult.IsError)
        {
            // Audit #31 — distinguish a retryable failure from a spent invite.
            // A weak password is rejected before the user is created, so re-arm the
            // invite for a retry. But if the target admin ALREADY exists, the invite
            // is spent and stays claimed. (Replays can't create a second admin anyway:
            // the unique username/email indexes reject them.)
            var adminAlreadyExists = await session.Query<ApplicationUser>()
                .AnyAsync(u => !u.IsDeleted && u.NormalizedEmail == invite.Email.ToUpperInvariant(), ct);
            if (!adminAlreadyExists)
            {
                // Retryable: re-arm the claim, unless a reissue took the open slot meanwhile
                // (then the newer invite is the one that counts).
                invite.UsedAt = null;
                invite.OpenSlot = PendingAdminInvite.OpenSlotValue;
                session.Store(invite);
                try
                {
                    await session.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (IsLostRace(ex))
                {
                    logger.LogInformation("Bootstrap-invite not re-armed: a newer invite was issued meanwhile.");
                }
            }
            return bootstrapResult.Errors;
        }

        logger.LogInformation(
            "Bootstrap-invite consumed. UserId={UserId} Email={MaskedEmail}",
            bootstrapResult.Value.UserId, LogPiiMasking.MaskEmail(invite.Email));

        return bootstrapResult.Value;
    }

    private static bool IsLostRace(Exception ex) => ex switch
    {
        JasperFx.ConcurrencyException => true,
        JasperFx.DocumentAlreadyExistsException => true, // Marten's shape of the open-slot 23505
        Marten.Exceptions.MartenCommandException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } => true,
        Npgsql.PostgresException { SqlState: "23505" } => true,
        _ => false,
    };

    private string BuildMagicLinkUrl(Realm realm, string plaintextToken)
    {
        // The link is built against the realm's canonical public host
        // (PrimaryDomain) — the same origin used for every other outbound
        // link in the realm. In Dev that's http://{host}:4300 (the SPA dev
        // server, where /bootstrap is served), in Prod https://{host}.
        var baseUrl = RealmPublicUrl.RealmPublicBaseUrl(realm);
        return $"{baseUrl}/bootstrap?token={Uri.EscapeDataString(plaintextToken)}";
    }
}
