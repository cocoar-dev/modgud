using Marten;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Infrastructure.Email;

namespace Cocoar.Auth.Api.Features.Dev;

/// <summary>
/// Development-only endpoints for testing and E2E.
/// Guarded at runtime by IsDevelopment() check — never accessible in Production.
/// </summary>
public static class DevEndpoints
{
    public static WebApplication MapDevEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/dev")
            .WithTags("Dev")
            .AllowAnonymous();

        // GET /api/dev/emails — List all sent emails (most recent first)
        group.MapGet("emails", (InMemoryEmailService emailService) =>
        {
            var emails = emailService.GetSentEmails();
            return Results.Ok(emails.Select(e => new
            {
                e.To,
                e.Subject,
                e.HtmlBody,
                SentAt = e.SentAt.ToString("O"),
            }));
        })
        .WithName("Dev_Emails");

        // GET /api/dev/emails/{to} — Get last email sent to address
        group.MapGet("emails/{to}", (string to, InMemoryEmailService emailService) =>
        {
            var email = emailService.GetLastEmailTo(to);
            if (email is null) return Results.NotFound();
            return Results.Ok(new
            {
                email.To,
                email.Subject,
                email.HtmlBody,
                SentAt = email.SentAt.ToString("O"),
            });
        })
        .WithName("Dev_EmailByRecipient");

        // DELETE /api/dev/emails — Clear all stored emails
        group.MapDelete("emails", (InMemoryEmailService emailService) =>
        {
            emailService.Clear();
            return Results.Ok(new { Message = "Emails cleared" });
        })
        .WithName("Dev_ClearEmails");

        // POST /api/dev/reset-mfa/{userName} — Force-disable all MFA for a user (test cleanup)
        group.MapPost("reset-mfa/{userName}", async (string userName, IDocumentSession session) =>
        {
            var user = await session.Query<ApplicationUser>()
                .FirstOrDefaultAsync(u => u.NormalizedUserName == userName.ToUpperInvariant());

            if (user is null) return Results.NotFound();

            user.TwoFactorEnabled = false;
            user.EmailOtpEnabled = false;
            user.AuthenticatorKey = null;
            session.Store(user);

            // Also reset SecurityData
            var securityData = await session.LoadAsync<UserSecurityData>(user.Id);
            if (securityData is not null)
            {
                securityData.TwoFactorEnabled = false;
                securityData.AuthenticatorKey = null;
                session.Store(securityData);
            }

            // Delete any pending OTP challenge
            var challenge = await session.LoadAsync<EmailOtpChallenge>(user.Id);
            if (challenge is not null) session.Delete(challenge);

            await session.SaveChangesAsync();

            return Results.Ok(new { Message = $"MFA reset for {userName}" });
        })
        .WithName("Dev_ResetMfa");

        return application;
    }
}
