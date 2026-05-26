using System.Security.Cryptography;
using System.Text;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.Realms;
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
/// + put into the Administratoren group via <see cref="IRealmAdminBootstrapper"/>.
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
    /// <para>Atomicity: invite-mark-used + user-create + role/group seed
    /// all share one Marten transaction. If the password rejected by
    /// Identity, the invite stays unused and can be retried.</para>
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
                    ["AppName"] = "Modgud",
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
                "Auth: Bootstrap-invite issued but email delivery failed. Realm={Realm} Email={MaskedEmail}. The plaintext URL is still on the issuer's side.",
                realm.Slug, LogPiiMasking.MaskEmail(normalizedEmail));
        }

        logger.LogInformation(
            "Auth: Bootstrap-invite issued. Realm={Realm} UserName={UserName} Email={MaskedEmail} ExpiresAt={ExpiresAt} IssuedBy={IssuedBy}",
            realm.Slug, normalizedUserName, LogPiiMasking.MaskEmail(normalizedEmail), invite.ExpiresAt, issuedBy ?? "(self/CLI)");

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
            // Don't mark the invite used — recipient can retry with a
            // valid password. The reuse-detection still fires when the
            // bootstrap eventually succeeds.
            return bootstrapResult.Errors;
        }

        invite.UsedAt = DateTimeOffset.UtcNow;
        session.Store(invite);
        await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "Auth: Bootstrap-invite consumed. UserName={UserName} Email={Email}",
            invite.UserName, invite.Email);

        return bootstrapResult.Value;
    }

    private string BuildMagicLinkUrl(Realm realm, string plaintextToken)
    {
        // Pick the realm's primary domain — first entry in Domains[]. In Dev
        // we fall back to localhost for the system realm so the link
        // actually opens in the developer's browser; for tenant realms in
        // Dev the operator is expected to point Host=… at the right realm
        // (acme.localhost etc.).
        string host;
        if (realm.Domains is { Length: > 0 })
        {
            host = realm.Domains[0];
        }
        else if (env.IsDevelopment())
        {
            host = "localhost";
        }
        else
        {
            host = realm.Slug + ".invalid";
        }

        // Public-facing URL: in Dev we use the SPA dev-server port (4300),
        // in Prod we trust IServerConfiguration.PublicUrl / AppUrl. Dev-port
        // detection: if AppUrl points at the API (9099), swap to 4300 because
        // that's where /bootstrap is served.
        string scheme;
        string? portSuffix = null;
        if (env.IsDevelopment())
        {
            scheme = "http";
            portSuffix = ":4300";
        }
        else
        {
            scheme = "https";
            // In prod the reverse-proxy fronts the same host on 443 — no
            // port suffix needed.
        }

        return $"{scheme}://{host}{portSuffix}/bootstrap?token={Uri.EscapeDataString(plaintextToken)}";
    }
}
