using System.Security.Claims;
using System.Security.Cryptography;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Sessions;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Account;

public static class MagicLinkEndpoints
{
    /// <summary><paramref name="ReturnUrl"/> threads a pending post-login
    /// continuation (e.g. a client app's /connect/authorize flow) through the
    /// e-mail round trip; it is validated as a same-origin path before being
    /// appended to the emailed URL as <c>?redirect=</c>.</summary>
    public record MagicLinkRequestDto(string Email, string? ReturnUrl = null);
    public record MagicLinkLoginDto(Guid UserId, string Token);

    public static WebApplication MapMagicLinkEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/magic-link")
            .WithTags("Magic Link")
            .AllowAnonymous();

        // POST /api/account/magic-link/request — Send login link via email
        group.MapPost("request", async (
            MagicLinkRequestDto request,
            IDocumentSession session,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IMagicLinkConfiguration config,
            IRealmProvisioningService realmSvc,
            IAuthSettings appSettings,
            IWebHostEnvironment env,
            HttpContext context,
            CancellationToken ct) =>
        {
            const string genericMessage = "If your email is registered, you will receive a login link.";

            // Check both platform config AND in-app settings
            if (!config.Enabled || !appSettings.MagicLinkSelfService)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            if (string.IsNullOrWhiteSpace(request.Email))
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Resolve the current realm — its primary domain is the link host.
            var realm = await context.ResolveCurrentRealmAsync(realmSvc, ct);
            if (realm is null)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            var user = await session.Query<ApplicationUser>()
                .FirstOrDefaultAsync(u => u.NormalizedEmail == request.Email.ToUpperInvariant() && !u.IsDeleted);

            if (user is null || !user.IsActive)
            {
                // Anti-timing: simulate work so response time matches the happy path
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Gate self-service magic-link on a verified email — sending a
            // login link to an unverified address would let a typo'd or
            // attacker-controlled mailbox bypass the password factor entirely.
            // Admin-side /api/admin/users/{id}/magic-link bypasses this on
            // purpose: it's the recovery channel for users who can't verify
            // themselves. Consuming any magic-link auto-confirms downstream.
            if (!user.EmailConfirmed)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Rate limiting: check for recent challenge for this user
            var existingChallenges = await session.Query<MagicLinkChallenge>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync();

            // Audit #25 follow-up — exclude already-consumed challenges. Since the
            // consume switched from Delete to a mark-consumed Store, a just-used link
            // leaves a non-expired row behind; without this filter it would spuriously
            // rate-limit the user's NEXT link request for up to RateLimitMinutes.
            var recentChallenge = existingChallenges
                .FirstOrDefault(c => !c.IsExpired && !c.IsConsumed &&
                    (DateTimeOffset.UtcNow - c.CreatedAt).TotalMinutes < config.RateLimitMinutes);

            if (recentChallenge is not null)
            {
                // Same jitter as every other branch — a rate-limited (hence known)
                // address must not return measurably faster than a fresh request.
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Clean up old challenges for this user
            foreach (var old in existingChallenges)
                session.Delete(old);

            // Generate token
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = MagicLinkChallenge.HashToken(token);

            var challenge = new MagicLinkChallenge
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = tokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(challenge);
            await session.SaveChangesAsync();

            // Build magic link URL — Vue frontend handles /magic-login route
            var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
            var encodedToken = Uri.EscapeDataString(token);
            var magicUrl = $"{appUrl}/magic-login?userId={user.Id}&token={encodedToken}";

            // Thread the pending continuation through the e-mail round trip so
            // a magic-link login can complete a client app's OIDC authorize
            // flow. Open-redirect guard mirrors the SPA's: only a
            // single-'/'-rooted same-origin path may ride along.
            if (Api.LoginRedirectGuard.IsSameOriginPath(request.ReturnUrl) && request.ReturnUrl != "/")
                magicUrl += $"&redirect={Uri.EscapeDataString(request.ReturnUrl!)}";

            await emailService.SendTemplatedEmailAsync(
                user.Email!,
                EmailTemplate.MagicLink,
                new Dictionary<string, string>
                {
                    ["AppName"] = await emailBranding.ResolveProductNameAsync(ct),
                    ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                    ["ActionUrl"] = magicUrl,
                    ["ExpirationMinutes"] = config.ExpirationMinutes.ToString(),
                });

            // Anti-timing: the success path does real work (DB writes + email send)
            // but used to skip the jitter every failure branch applies — so a
            // registered, confirmed, active address returned on a different timing
            // profile than an unknown one, leaking which emails exist. Apply the
            // same jitter here so the timing carries no enumeration signal.
            await AntiTimingDelayAsync();

            return Results.Ok(new { Message = genericMessage });
        })
        .WithName("MagicLink_Request")
        // RATE-01 — 5 requests per hour per IP. Per-user rate limit lives
        // in the magic-link service itself; this is the upstream IP cap
        // that prevents enum-spam against the SMTP path.
        .RequireRateLimiting("magic-link");

        // POST /api/account/magic-link/login — Validate token and sign in
        group.MapPost("login", async (
            MagicLinkLoginDto request,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ISecurityAuditLog securityAudit,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);

            var tokenHash = MagicLinkChallenge.HashToken(request.Token);

            // Find matching challenge
            var challenge = await session.Query<MagicLinkChallenge>()
                .FirstOrDefaultAsync(c => c.UserId == request.UserId && c.TokenHash == tokenHash);

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (challenge is null || challenge.IsExpired || challenge.IsConsumed)
            {
                securityAudit.Record(new SecurityAuditRecord
                {
                    EventType = AuditEvents.MagicLinkInvalid,
                    Level = "Warning",
                    Ip = ip,
                    Status = "rejected",
                    Reason = "invalid or expired token",
                    Message = "Magic-link login failed — invalid or expired token",
                });
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.Failure);
                if (challenge is not null) { session.Delete(challenge); await session.SaveChangesAsync(); }
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
            }

            // Load via the UserManager finder (not a raw session load) so the
            // in-memory user is hydrated with the AUTHORITATIVE security data from
            // UserSecurityData (stamp, 2FA, lockout). The cookie minted below AND
            // the user.TwoFactorEnabled TOTP step-up gate further down must not
            // read a stale ApplicationUser mirror; FindByIdAsync also filters
            // IsDeleted (the explicit gate stays as defense-in-depth).
            var user = await userManager.FindByIdAsync(request.UserId.ToString());
            if (user is null || user.IsDeleted || !user.IsActive)
            {
                securityAudit.Record(new SecurityAuditRecord
                {
                    EventType = AuditEvents.LoginFailedUnknownUser,
                    Level = "Warning",
                    Ip = ip,
                    Status = "rejected",
                    Reason = "user not found or inactive",
                    Message = "Magic-link login failed — user not found or inactive",
                });
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.Failure);
                session.Delete(challenge);
                await session.SaveChangesAsync();
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
            }

            // A successfully consumed magic-link is proof the user controls
            // the mailbox we sent it to. Auto-confirm the email so downstream
            // self-service flows (forgot-password, self-magic-link) unblock.
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                session.Store(user);

                // EmailConfirmed lives on the ApplicationUser doc, not on
                // UserView, but the admin grid surfaces it (joined by
                // SignalRProjectionDispatchHandler from the fresh ApplicationUser).
                // Append a no-op UserUpdatedEvent so the UserView slice fires
                // its RaiseSideEffects → SignalR push → admin grids learn the
                // new state live without a manual reload. All-None Optionals =
                // Apply does nothing structurally; only the dispatch matters.
                session.Events.Append(user.Id, new Modgud.Domain.Users.Events.UserUpdatedEvent(
                    Id: user.Id,
                    Firstname: default,
                    Lastname: default,
                    Acronym: default,
                    Email: default));
            }

            // Mark the challenge consumed (one-time use) — regardless of whether a
            // second factor still has to be presented below. Audit #25: this is a
            // version-checked Store, not a Delete. Marten does NOT enforce optimistic
            // concurrency on deletes, so two concurrent redemptions of the same link
            // would both delete-and-proceed; a Store of the ConsumedAt flag makes the
            // losing racer's SaveChangesAsync throw a ConcurrencyException (caught
            // below → 401), while a later replay is rejected by the IsConsumed gate
            // above. The row is reaped by expiry / the per-user request sweep.
            challenge.ConsumedAt = DateTimeOffset.UtcNow;
            session.Store(challenge);

            // Audit M1: a magic-link must NOT bypass a user's TOTP second factor.
            // Mailbox possession alone is exactly the channel TOTP is meant to
            // survive. If TOTP is enabled, establish the same partial-2FA state a
            // password login sets on RequiresTwoFactor and let /api/account/mfa/login
            // complete the second factor. Only TOTP steps up — offering email-OTP
            // here would defeat the purpose (the mailbox is already in play).
            if (user.TwoFactorEnabled)
            {
                try
                {
                    await session.SaveChangesAsync();
                }
                catch (JasperFx.ConcurrencyException)
                {
                    // Audit #25 — a concurrent redemption already consumed this
                    // one-time link (version-checked delete lost the race). Treat as
                    // invalid; do NOT establish the partial-2FA state for a stale link.
                    ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.Failure);
                    return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
                }

                var twoFactorIdentity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
                twoFactorIdentity.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString()));
                await context.SignInAsync(
                    IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(twoFactorIdentity));

                Serilog.Log.Information(
                    "Magic-link consumed; TOTP step-up required. UserId={UserId} IP={IP}", user.Id, ip);
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.TwoFactorRequired);
                return Results.Ok(new { RequiresMfa = true, MfaMethods = new List<string> { "totp" } });
            }

            // Audit marker — magic-link login success (Phase 1). No IP on the event
            // (the Sessions feature owns IP/device); rides the same transaction.
            // Only on an actual full login (no second factor pending).
            session.Events.Append(user.Id, new Modgud.Authentication.Events.UserLoggedInEvent(
                user.Id, IpAddress: null, Method: ModgudMeters.LoginMethod.MagicLink));

            try
            {
                await session.SaveChangesAsync();
            }
            catch (JasperFx.ConcurrencyException)
            {
                // Audit #25 — a concurrent redemption already consumed this one-time
                // link. Don't mint a second full session for a stale link.
                ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.Failure);
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
            }

            // Sign in — Magic Link is always persistent; user can request a new link anytime.
            await signInManager.SignInAsync(user, isPersistent: true);


            Serilog.Log.Information("Magic link login successful. UserId={UserId} IP={IP}", user.Id, ip);
            ModgudMeters.RecordLogin(ModgudMeters.LoginMethod.MagicLink, ModgudMeters.LoginOutcome.Success);
            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("MagicLink_Login");

        return application;
    }

    /// <summary>
    /// Random delay (100-300ms) to prevent timing side-channel attacks
    /// that could reveal whether an email address exists in the system.
    /// </summary>
    private static async Task AntiTimingDelayAsync()
    {
        // CA5394 / SCS0005: Random.Shared is correct here — this is a
        // non-security jitter to obscure timing channels, not a token
        // generator. Real security tokens in this slice (magic-link,
        // email-verification, OTP, invite, recovery) all use
        // RandomNumberGenerator.GetBytes(). Crypto RNG would be needlessly
        // expensive for sub-millisecond delay generation that has no
        // secrecy requirement.
#pragma warning disable CA5394, SCS0005
        var delayMs = Random.Shared.Next(100, 300);
#pragma warning restore CA5394, SCS0005
        await Task.Delay(delayMs);
    }
}
