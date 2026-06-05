using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authorization.AspNetCore;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Identity;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin-only magic link sending. Always available regardless of MagicLinkSelfService setting.
/// Used for onboarding new users and as emergency access.
/// </summary>
public static class AdminMagicLinkEndpoints
{
    public static WebApplication MapAdminMagicLinkEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/users")
            .WithTags("Admin Magic Link")
            .RequireAuthorization()
            .RequiresPermission("user:write");

        // POST /api/admin/users/{id}/magic-link — Send a magic link to the user
        group.MapPost("{id}/magic-link", async (
            string id,
            IDocumentSession session,
            IEmailService emailService,
            IMagicLinkConfiguration config,
            IRealmProvisioningService realmSvc,
            IWebHostEnvironment env,
            HttpContext context,
            CancellationToken ct) =>
        {
            var userId = BuildingBlocks.Helper.ShortGuid.Decode(id);
            var user = await session.LoadAsync<ApplicationUser>(userId);

            if (user is null || !user.IsActive)
                return Results.NotFound(new { Message = "User not found or inactive" });

            if (string.IsNullOrEmpty(user.Email))
                return Results.Json(new { Message = "User has no email address" }, statusCode: 400);

            // The link host is the current realm's primary domain. Unlike the
            // anti-enumeration self-service path, this is an authenticated admin
            // action — surface a clear error if the realm can't be resolved
            // rather than silently sending a broken link.
            var realm = await context.ResolveCurrentRealmAsync(realmSvc, ct);
            if (realm is null)
                return Results.Json(new { Message = "Could not resolve the current realm." }, statusCode: 500);

            // Clean up old challenges for this user
            var existingChallenges = await session.Query<MagicLinkChallenge>()
                .Where(c => c.UserId == user.Id)
                .ToListAsync();
            foreach (var old in existingChallenges)
                session.Delete(old);

            // Generate token
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

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

            // Build magic link URL
            var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
            var encodedToken = Uri.EscapeDataString(token);
            var magicUrl = $"{appUrl}/magic-login?userId={user.Id}&token={encodedToken}";

            await emailService.SendTemplatedEmailAsync(
                user.Email,
                EmailTemplate.MagicLink,
                new Dictionary<string, string>
                {
                    ["AppName"] = "Modgud",
                    ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                    ["ActionUrl"] = magicUrl,
                    ["ExpirationMinutes"] = config.ExpirationMinutes.ToString(),
                });

            Serilog.Log.Information("Admin: Magic link sent to {UserId} ({MaskedEmail})", user.Id, LogPiiMasking.MaskEmail(user.Email));
            return Results.Ok(new { Message = "Magic link sent" });
        })
        .WithName("Admin_SendMagicLink");

        return application;
    }
}
