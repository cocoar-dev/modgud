using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// ADR-0010 — anonymous, cookieless OTP-request endpoint for native passwordless
/// login. The web/2FA email-OTP request (<c>EmailOtpEndpoints</c>) requires a
/// partial-auth cookie because email-OTP is a SECOND factor there; a native
/// client has no such cookie, so this issues a code with email-OTP acting as a
/// PRIMARY factor.
///
/// <para>Three gates / mitigations, all mirrored from the magic-link request
/// endpoint: the per-realm <c>NativeGrants</c> master flag (default OFF);
/// anti-timing jitter + a uniform response so the endpoint is not an
/// email-existence oracle; and the same per-IP SMTP rate limit. The code is then
/// redeemed at <c>/connect/token</c> via <c>grant_type=urn:cocoar:otp</c>.</para>
/// </summary>
public static class NativeOtpEndpoints
{
    public record NativeOtpRequestDto(string Email);

    public static WebApplication MapNativeOtpEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/native/otp")
            .WithTags("Native Auth")
            .AllowAnonymous();

        // POST /api/account/native/otp/request — email a one-time login code.
        group.MapPost("request", async (
            NativeOtpRequestDto request,
            IDocumentSession session,
            IEmailOtpService emailOtpService,
            CancellationToken ct) =>
        {
            const string genericMessage = "If your email is registered, you will receive a verification code.";

            // Per-realm master gate (default OFF). The injected session is
            // tenant-scoped (RealmMiddleware), so this reads the calling realm's
            // doc; a null section reads as disabled.
            var settings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
            if (settings?.NativeGrants is null || !settings.NativeGrants.Enabled)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                var user = await session.Query<ApplicationUser>()
                    .FirstOrDefaultAsync(
                        u => u.NormalizedEmail == request.Email.ToUpperInvariant() && !u.IsDeleted, ct);

                if (user is not null)
                {
                    // Discard the result deliberately: an unknown / ineligible /
                    // rate-limited user must be indistinguishable on the wire and
                    // in timing from a successful send (anti-enumeration). The
                    // service still enforces confirmed-mailbox + per-user rate limit.
                    _ = await emailOtpService.RequestNativeOtpAsync(user.Id, ct);
                }
            }

            // Same jitter on every branch (incl. success, which did real work) so
            // response time carries no signal about whether the email exists.
            await AntiTimingDelayAsync();
            return Results.Ok(new { Message = genericMessage });
        })
        .WithName("NativeOtp_Request")
        // Same per-IP SMTP cap class as magic-link / email-verification: 5/hour.
        .RequireRateLimiting("native-otp");

        return application;
    }

    /// <summary>
    /// Random delay (100-300ms) to mask whether an email exists. Non-security
    /// jitter — Random.Shared is correct here (the real OTP secret uses CSPRNG
    /// in EmailOtpService).
    /// </summary>
    private static async Task AntiTimingDelayAsync()
    {
#pragma warning disable CA5394, SCS0005
        var delayMs = Random.Shared.Next(100, 300);
#pragma warning restore CA5394, SCS0005
        await Task.Delay(delayMs);
    }
}
