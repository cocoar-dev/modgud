using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.Applications;

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
            HttpContext httpContext,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            IEmailOtpService emailOtpService,
            IPasswordlessUserFactory passwordlessUserFactory,
            UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            const string genericMessage = "If your email is registered, you will receive a verification code.";

            // Per-(App⊕realm) master gate (default OFF), ADR-0011. Host-time: the
            // begin request carries no client_id, so the App (if any) comes from
            // the request Host (an Application subdomain). A null section reads as
            // disabled; an existing realm with no Application resolves to its realm
            // setting unchanged.
            var settings = await settingsResolver.ResolveForRequestAsync(httpContext, clientId: null, ct);
            if (settings.NativeGrants is null || !settings.NativeGrants.Enabled)
            {
                await AntiTimingDelayAsync();
                return Results.Ok(new { Message = genericMessage });
            }

            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                // Anti-enumeration: EVERY branch returns the same body + jitter and
                // discards results. With the JIT posture an unknown email gets a
                // registration code too, so "code sent" leaks nothing about
                // existence (ADR-0011 OQ3 — JIT is anti-enumeration-safe).
                await IssueOtpForRequestAsync(
                    request.Email, settings.SelfRegPosture, session, userManager,
                    emailOtpService, passwordlessUserFactory, ct);
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
    /// Routes a native OTP request to the right issue path (ADR-0010 login +
    /// ADR-0011 JIT registration). All outcomes are silent (results discarded) so
    /// the caller's uniform response + jitter is the only observable — no
    /// email-existence oracle.
    /// <list type="bullet">
    ///   <item>Known confirmed user → native login OTP.</item>
    ///   <item>Known passwordless still-unconfirmed user + JIT posture → re-issue
    ///   the registration OTP (resend for an in-progress sign-up).</item>
    ///   <item>Unknown email + JIT posture → create a passwordless user and issue
    ///   the registration OTP.</item>
    ///   <item>Otherwise (no JIT posture, or a password-bearing unconfirmed
    ///   account) → nothing (the web verification flow owns those).</item>
    /// </list>
    /// </summary>
    private static async Task IssueOtpForRequestAsync(
        string email,
        SelfRegPosture? posture,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        IEmailOtpService emailOtpService,
        IPasswordlessUserFactory passwordlessUserFactory,
        CancellationToken ct)
    {
        var user = await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant() && !u.IsDeleted, ct);

        var action = Decide(
            userExists: user is not null,
            emailConfirmed: user?.EmailConfirmed ?? false,
            hasPassword: !string.IsNullOrEmpty(user?.PasswordHash),
            posture);

        switch (action)
        {
            case NativeOtpAction.Login:
                _ = await emailOtpService.RequestNativeOtpAsync(user!.Id, ct);
                break;
            case NativeOtpAction.ResendRegistration:
                _ = await emailOtpService.RequestNativeRegistrationOtpAsync(user!.Id, ct);
                break;
            case NativeOtpAction.CreateAndRegister:
                var created = await passwordlessUserFactory.CreateAsync(email, ct);
                if (created is not null)
                    _ = await emailOtpService.RequestNativeRegistrationOtpAsync(created.Id, ct);
                break;
            case NativeOtpAction.None:
            default:
                break;
        }
    }

    public enum NativeOtpAction { None, Login, ResendRegistration, CreateAndRegister }

    /// <summary>
    /// Pure routing decision for a native OTP request (ADR-0010 login + ADR-0011
    /// JIT registration). Security-relevant: JIT registration fires ONLY under the
    /// <see cref="SelfRegPosture.JitOnOtp"/> posture, and a password-bearing
    /// unconfirmed account is never served a native code (it must verify via the
    /// web link).
    /// </summary>
    public static NativeOtpAction Decide(bool userExists, bool emailConfirmed, bool hasPassword, SelfRegPosture? posture)
    {
        var jit = posture == SelfRegPosture.JitOnOtp;

        if (userExists)
        {
            if (emailConfirmed) return NativeOtpAction.Login;
            // Unconfirmed: only a passwordless in-progress JIT sign-up may resend.
            if (jit && !hasPassword) return NativeOtpAction.ResendRegistration;
            return NativeOtpAction.None;
        }

        return jit ? NativeOtpAction.CreateAndRegister : NativeOtpAction.None;
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
