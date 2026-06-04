using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// Email-verification flow for already-existing users. With Phase-1 defaults
/// (admin-created + self-reg + bootstrap all start verified), users only land
/// in an unverified state if an admin explicitly cleared the flag — they can
/// then password-login, see the in-app banner, and re-verify with one click.
///
/// Two endpoints, both narrow on purpose:
///   <list type="bullet">
///   <item>POST /api/account/email/send-verification — authenticated only.
///         Issues a token for the logged-in user (banner-driven 1-click).
///         No anonymous lookup branch on purpose: enumeration-via-mailbox
///         attack surface with marginal recovery value, since unverified
///         users can password-login and trigger the same flow.</item>
///   <item>POST /api/account/email/verify — anonymous consume. Required
///         to be open because the verification mail is clicked from a
///         fresh browser context that doesn't carry the Identity cookie.</item>
///   </list>
///
/// Distinct from the SelfRegistration pending-doc verify and the profile
/// change-request verify — those carry side-effects (group attachment,
/// admin-approval workflow) that don't apply here.
/// </summary>
public static class EmailVerificationEndpoints
{
    public record ConsumeVerificationRequest(string Token);

    public static WebApplication MapEmailVerificationEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/email")
            .WithTags("Email Verification");

        // POST /api/account/email/send-verification — authenticated 1-click
        // from the in-app unverified-email banner. No anonymous variant: a
        // user who can't log in goes through admin-driven recovery (admin
        // sends a magic-link from the user list, consuming auto-verifies).
        group.MapPost("send-verification", [Authorize] async (
            IDocumentSession session,
            IEmailService emailService,
            UserManager<ApplicationUser> userManager,
            IServerConfiguration conf,
            IWebHostEnvironment env,
            HttpContext context) =>
        {
            const string genericMessage = "If the account exists and the email matches, a verification link was sent.";

            var user = await userManager.GetUserAsync(context.User);

            if (user is null || !user.IsActive || user.IsDeleted || string.IsNullOrEmpty(user.Email))
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Already verified — silently no-op so the caller can't probe
            // verification state.
            if (user.EmailConfirmed)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Replace any previous outstanding challenge for this user;
            // only the latest link should be live.
            var existing = await session.Query<EmailVerificationChallenge>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync();
            foreach (var old in existing) session.Delete(old);

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = HashToken(token);

            var challenge = new EmailVerificationChallenge
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = tokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(EmailVerificationChallenge.ExpirationHours),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            session.Store(challenge);
            await session.SaveChangesAsync();

            var appUrl = (conf.PublicUrl ?? (env.IsDevelopment() ? "http://localhost:4300" : conf.AppUrl)).TrimEnd('/');
            var encodedToken = Uri.EscapeDataString(token);
            // type=account discriminates from the existing profile-change
            // and self-registration verify flows that share /verify-email.
            var verifyUrl = $"{appUrl}/verify-email?type=account&token={encodedToken}";

            await emailService.SendTemplatedEmailAsync(
                user.Email,
                EmailTemplate.EmailVerification,
                new Dictionary<string, string>
                {
                    ["AppName"] = "Modgud",
                    ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                    ["ActionUrl"] = verifyUrl,
                    ["ExpirationHours"] = EmailVerificationChallenge.ExpirationHours.ToString(),
                });

            return Results.Ok(new { Message = genericMessage });
        })
        .WithName("EmailVerification_Send")
        .RequireRateLimiting("email-verification");

        // POST /api/account/email/verify — anonymous consume. The mail link
        // opens in a fresh browser context that doesn't carry the Identity
        // cookie, so this endpoint must stay open. Possession of a valid
        // token IS the authentication.
        group.MapPost("verify", [AllowAnonymous] async (
            ConsumeVerificationRequest request,
            IDocumentSession session) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 400);

            var tokenHash = HashToken(request.Token);

            var challenge = await session.Query<EmailVerificationChallenge>()
                .FirstOrDefaultAsync(c => c.TokenHash == tokenHash);

            if (challenge is null || challenge.IsExpired)
            {
                if (challenge is not null) { session.Delete(challenge); await session.SaveChangesAsync(); }
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 400);
            }

            var user = await session.LoadAsync<ApplicationUser>(challenge.UserId);
            if (user is null || user.IsDeleted)
            {
                session.Delete(challenge);
                await session.SaveChangesAsync();
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 400);
            }

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
            session.Delete(challenge);
            await session.SaveChangesAsync();

            Serilog.Log.Information("EmailVerification: confirmed user {UserId}", user.Id);
            return Results.Ok(new { Message = "Email verified" });
        })
        .WithName("EmailVerification_Consume");

        return application;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Anti-timing jitter — matches the MagicLink endpoints' helper.</summary>
    private static async Task AntiTimingDelayAsync()
    {
#pragma warning disable CA5394, SCS0005
        var delayMs = Random.Shared.Next(100, 300);
#pragma warning restore CA5394, SCS0005
        await Task.Delay(delayMs);
    }
}
