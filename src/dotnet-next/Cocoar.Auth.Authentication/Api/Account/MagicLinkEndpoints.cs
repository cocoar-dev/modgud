using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Infrastructure.Email;

namespace Cocoar.Auth.Authentication.Api.Account;

public static class MagicLinkEndpoints
{
    public record MagicLinkRequestDto(string Email);
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
            IMagicLinkConfiguration config,
            IServerConfiguration conf,
            IAuthSettings appSettings,
            IWebHostEnvironment env,
            HttpContext context) =>
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

            var user = await session.Query<ApplicationUser>()
                .FirstOrDefaultAsync(u => u.NormalizedEmail == request.Email.ToUpperInvariant() && !u.IsDeleted);

            if (user is null || !user.IsActive)
            {
                // Anti-timing: simulate work so response time matches the happy path
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            // Rate limiting: check for recent challenge for this user
            var existingChallenges = await session.Query<MagicLinkChallenge>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync();

            var recentChallenge = existingChallenges
                .FirstOrDefault(c => !c.IsExpired &&
                    (DateTimeOffset.UtcNow - c.CreatedAt).TotalMinutes < config.RateLimitMinutes);

            if (recentChallenge is not null)
                return Results.Ok(new { Message = "If your email is registered, you will receive a login link." });

            // Clean up old challenges for this user
            foreach (var old in existingChallenges)
                session.Delete(old);

            // Generate token
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = HashToken(token);

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
            var appUrl = (conf.PublicUrl ?? (env.IsDevelopment() ? "http://localhost:4300" : conf.AppUrl)).TrimEnd('/');
            var encodedToken = Uri.EscapeDataString(token);
            var magicUrl = $"{appUrl}/magic-login?userId={user.Id}&token={encodedToken}";

            await emailService.SendTemplatedEmailAsync(
                user.Email!,
                EmailTemplate.MagicLink,
                new Dictionary<string, string>
                {
                    ["AppName"] = "Cocoar.Auth",
                    ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                    ["ActionUrl"] = magicUrl,
                    ["ExpirationMinutes"] = config.ExpirationMinutes.ToString(),
                });

            return Results.Ok(new { Message = "If your email is registered, you will receive a login link." });
        })
        .WithName("MagicLink_Request");

        // POST /api/account/magic-link/login — Validate token and sign in
        group.MapPost("login", async (
            MagicLinkLoginDto request,
            IDocumentSession session,
            SignInManager<ApplicationUser> signInManager,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);

            var tokenHash = HashToken(request.Token);

            // Find matching challenge
            var challenge = await session.Query<MagicLinkChallenge>()
                .FirstOrDefaultAsync(c => c.UserId == request.UserId && c.TokenHash == tokenHash);

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (challenge is null || challenge.IsExpired)
            {
                Serilog.Log.Warning("Auth: Magic link login failed — invalid/expired token. IP={IP}", ip);
                if (challenge is not null) { session.Delete(challenge); await session.SaveChangesAsync(); }
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
            }

            // Load user
            var user = await session.LoadAsync<ApplicationUser>(request.UserId);
            if (user is null || user.IsDeleted || !user.IsActive)
            {
                Serilog.Log.Warning("Auth: Magic link login failed — user not found/inactive. IP={IP}", ip);
                session.Delete(challenge);
                await session.SaveChangesAsync();
                return Results.Json(new { Message = "Invalid or expired link" }, statusCode: 401);
            }

            // Delete challenge (one-time use)
            session.Delete(challenge);
            await session.SaveChangesAsync();

            // Sign in — bypasses MFA (Magic Link IS the authentication)
            // Magic Link is always persistent — user can request a new link anytime
            await signInManager.SignInAsync(user, isPersistent: true);

            Serilog.Log.Information("Auth: Magic link login successful. User={UserName} IP={IP}", user.UserName, ip);
            return Results.Ok(new { Message = "Login successful" });
        })
        .WithName("MagicLink_Login");

        return application;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Random delay (100-300ms) to prevent timing side-channel attacks
    /// that could reveal whether an email address exists in the system.
    /// </summary>
    private static async Task AntiTimingDelayAsync()
    {
        var delayMs = Random.Shared.Next(100, 300);
        await Task.Delay(delayMs);
    }
}
