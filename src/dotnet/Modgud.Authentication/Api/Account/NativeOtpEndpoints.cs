using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.SelfRegistration;
using Modgud.Domain.Applications;
using Modgud.Infrastructure.Persistence.Tenancy;

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
    /// <summary><paramref name="InviteCode"/> is optional and only consulted
    /// under the <see cref="SelfRegPosture.InviteCode"/> posture (ADR-0012); it
    /// is ignored for every other posture, so the field is backward-compatible.</summary>
    public record NativeOtpRequestDto(string Email, string? InviteCode = null);

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
            IRegistrationInviteService inviteService,
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
                    request.Email, request.InviteCode, httpContext.GetApplicationId(),
                    settings.SelfRegPosture, session, userManager,
                    emailOtpService, passwordlessUserFactory, inviteService, ct);
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
    ///   <item>Unknown email + InviteCode posture (ADR-0012) → create + register
    ///   ONLY if a valid, unused, unexpired, app-matching code is presented; the
    ///   code is consumed atomically before creation. A missing/invalid/used/
    ///   expired/mismatched code is silently a no-op (indistinguishable from
    ///   <see cref="SelfRegPosture.Off"/>).</item>
    ///   <item>Otherwise (no self-reg posture, or a password-bearing unconfirmed
    ///   account) → nothing (the web verification flow owns those).</item>
    /// </list>
    /// </summary>
    private static async Task IssueOtpForRequestAsync(
        string email,
        string? inviteCode,
        Guid? appId,
        SelfRegPosture? posture,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        IEmailOtpService emailOtpService,
        IPasswordlessUserFactory passwordlessUserFactory,
        IRegistrationInviteService inviteService,
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
                await CreateAndRegisterAsync(
                    email, inviteCode, appId, posture,
                    emailOtpService, passwordlessUserFactory, inviteService, ct);
                break;
            case NativeOtpAction.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Materialises a new passwordless user for an unknown email and issues the
    /// registration OTP. Under <see cref="SelfRegPosture.InviteCode"/> this is
    /// gated on consuming a valid invite code FIRST (ADR-0012, D4/§5): the code is
    /// consumed atomically (single-use, optimistic-concurrency) before the user
    /// exists, which closes the bearer-code race (consuming at redeem would let two
    /// requests each create an account before either redeems). Any code failure is
    /// a silent no-op so the path stays anti-enumeration-safe.
    /// </summary>
    private static async Task CreateAndRegisterAsync(
        string email,
        string? inviteCode,
        Guid? appId,
        SelfRegPosture? posture,
        IEmailOtpService emailOtpService,
        IPasswordlessUserFactory passwordlessUserFactory,
        IRegistrationInviteService inviteService,
        CancellationToken ct)
    {
        Guid? consumedInviteId = null;
        if (posture == SelfRegPosture.InviteCode)
        {
            // Codes are app-bound (D3). With no App in context there is nothing to
            // validate against → silent no-op.
            if (appId is null)
                return;
            var consume = await inviteService.TryConsumeAsync(appId.Value, email, inviteCode ?? string.Empty, ct);
            if (!consume.IsConsumed)
                return; // missing/invalid/used/expired/mismatched/lost-race → no-op
            consumedInviteId = consume.InviteId;
        }

        var created = await passwordlessUserFactory.CreateAsync(email, ct);
        if (created is null)
            return; // TOCTOU race lost; under InviteCode the code stays consumed (benign, swept later)

        if (consumedInviteId is { } inviteId)
            await inviteService.AttachConsumerAsync(inviteId, created.Id, ct);

        _ = await emailOtpService.RequestNativeRegistrationOtpAsync(created.Id, ct);
    }

    public enum NativeOtpAction { None, Login, ResendRegistration, CreateAndRegister }

    /// <summary>
    /// Pure routing decision for a native OTP request (ADR-0010 login + ADR-0011
    /// JIT registration + ADR-0012 invite-code registration). Security-relevant:
    /// self-registration fires ONLY under <see cref="SelfRegPosture.JitOnOtp"/> or
    /// <see cref="SelfRegPosture.InviteCode"/>, and a password-bearing unconfirmed
    /// account is never served a native code (it must verify via the web link).
    ///
    /// <para>JIT and InviteCode route email-state identically here; the difference
    /// — that InviteCode requires a valid code — is enforced downstream in
    /// <see cref="CreateAndRegisterAsync"/> because code validity needs the DB and
    /// this method stays pure. So <see cref="NativeOtpAction.CreateAndRegister"/>
    /// under InviteCode means "create IF a valid code is presented".</para>
    /// </summary>
    public static NativeOtpAction Decide(bool userExists, bool emailConfirmed, bool hasPassword, SelfRegPosture? posture)
    {
        var selfReg = posture is SelfRegPosture.JitOnOtp or SelfRegPosture.InviteCode;

        if (userExists)
        {
            // A confirmed user always just logs in — under InviteCode the code is
            // ignored and NOT consumed (D11); app-side resource joins use the app's
            // own accept-invite path.
            if (emailConfirmed) return NativeOtpAction.Login;
            // Unconfirmed: only a passwordless in-progress self-reg sign-up may resend.
            if (selfReg && !hasPassword) return NativeOtpAction.ResendRegistration;
            return NativeOtpAction.None;
        }

        return selfReg ? NativeOtpAction.CreateAndRegister : NativeOtpAction.None;
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
