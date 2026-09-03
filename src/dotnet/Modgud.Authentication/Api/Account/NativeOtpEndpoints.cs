using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Registration;
using Modgud.Authentication.SelfRegistration;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Authentication.RateLimiting;

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
    /// is ignored for every other posture, so the field is backward-compatible.
    /// <paramref name="FirstName"/>/<paramref name="LastName"/> are collected when
    /// the (App⊕realm) registration-field policy requires them (PR #95).</summary>
    public record NativeOtpRequestDto(string Email, string? FirstName = null, string? LastName = null, string? InviteCode = null);

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
            IRegistrationPipeline registrationPipeline,
            IRegistrationInviteService inviteService,
            UserManager<ApplicationUser> userManager,
            ILoggerFactory loggerFactory,
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
                // Surfaced LOUDLY, not as a silent uniform 200. Whether native grants
                // are enabled is a realm/App configuration state, NOT a signal about
                // whether a given email exists — so returning an explicit error here
                // leaks nothing the email-existence branch below must protect. It
                // matches the native passkey-begin and the /connect/token grant, both
                // of which already reject loudly when the realm flag is off. The old
                // silent no-op meant a misconfigured realm looked exactly like "email
                // sent" — no mail, no error, no way to diagnose. The WARN lands in the
                // per-realm error feed so the admin sees the misconfiguration.
                loggerFactory.CreateLogger("Modgud.Authentication.NativeOtp").LogWarning(
                    "Native OTP requested but native passwordless grants are disabled for this realm/App. " +
                    "Enable them under Realm Settings → Native Passwordless Grants (and grant the client the " +
                    "matching gt:urn:cocoar:otp permission).");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "NativeGrants.Disabled",
                    detail: "Native passwordless sign-in is not enabled for this realm.");
            }

            // Required-field gate (configurable per App⊕realm). Surfaced as a hard
            // 400 BEFORE the uniform branch: name-presence is independent of whether
            // the email exists, so this leaks nothing. The native client renders the
            // fields /api/app-info reports as required, so it always sends them; an
            // existing user signing in re-sends them (ignored on login). Username is
            // never enforced here — the native username is always the email.
            if (RegistrationFieldsPolicy.FirstMissingRequiredName(
                    settings.RegistrationFields, request.FirstName, request.LastName) is { } missing)
            {
                return Results.BadRequest(new { error = $"{missing} is required." });
            }

            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                // Anti-enumeration: EVERY branch returns the same body + jitter and
                // discards results. With the JIT posture an unknown email gets a
                // registration code too, so "code sent" leaks nothing about
                // existence (ADR-0011 OQ3 — JIT is anti-enumeration-safe).
                await IssueOtpForRequestAsync(
                    request.Email, request.FirstName, request.LastName,
                    request.InviteCode, httpContext.GetApplicationId(),
                    settings.SelfRegPosture, session, userManager,
                    emailOtpService, registrationPipeline, inviteService, ct);
            }

            // Same jitter on every branch (incl. success, which did real work) so
            // response time carries no signal about whether the email exists.
            await AntiTimingDelayAsync();
            return Results.Ok(new { Message = genericMessage });
        })
        .WithName("NativeOtp_Request")
        // Same per-IP SMTP cap class as magic-link / email-verification: 5/hour.
        .RequireAuthRateLimit(AuthRateLimitPolicy.NativeOtp, target: ctx => ctx.Argument<NativeOtpRequestDto>()?.Email);

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
    ///   <item>Unknown email + JIT posture → enter the registration pipeline
    ///   (ADR 0006: pending record + code; NO user until the code is proved).</item>
    ///   <item>Unknown email + InviteCode posture (ADR-0012) → enter the pipeline
    ///   ONLY if a valid, unused, unexpired, app-matching code is presented; the
    ///   code is consumed atomically before anything is written. A missing/invalid/used/
    ///   expired/mismatched code is silently a no-op (indistinguishable from
    ///   <see cref="SelfRegPosture.Off"/>).</item>
    ///   <item>Otherwise (no self-reg posture, or a password-bearing unconfirmed
    ///   account) → nothing (the web verification flow owns those).</item>
    /// </list>
    /// </summary>
    internal static async Task IssueOtpForRequestAsync(
        string email,
        string? firstName,
        string? lastName,
        string? inviteCode,
        Guid? appId,
        SelfRegPosture? posture,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        IEmailOtpService emailOtpService,
        IRegistrationPipeline registrationPipeline,
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
                    email, firstName, lastName, inviteCode, appId, posture,
                    registrationPipeline, inviteService, ct);
                break;
            case NativeOtpAction.None:
            default:
                break;
        }
    }

    /// <summary>
    /// ADR 0006 — enters the registration pipeline for an unknown email: a pending
    /// record is written and the registration code mailed; NO user exists until the
    /// code is proved at redeem. Under <see cref="SelfRegPosture.InviteCode"/> this is
    /// gated on consuming a valid invite code FIRST (ADR-0012, D4/§5): the code is
    /// consumed atomically (single-use, optimistic-concurrency) before anything is
    /// written, which closes the bearer-code race. Any code failure is a silent no-op
    /// so the path stays anti-enumeration-safe.
    /// </summary>
    private static async Task CreateAndRegisterAsync(
        string email,
        string? firstName,
        string? lastName,
        string? inviteCode,
        Guid? appId,
        SelfRegPosture? posture,
        IRegistrationPipeline registrationPipeline,
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

        // The full email is the username: unique per realm, collision-free.
        var trimmed = email.Trim();
        _ = await registrationPipeline.RequestAsync(new RegistrationRequest(
            Email: trimmed,
            UserName: trimmed,
            Firstname: firstName,
            Lastname: lastName,
            PasswordHash: null,
            ProofKind: RegistrationProofKind.Code,
            Source: posture == SelfRegPosture.InviteCode ? RegistrationSources.NativeInvite : RegistrationSources.NativeJit,
            ApplicationId: appId,
            ConsumedInviteId: consumedInviteId), ct);
        // Cooldown / lost race → no code sent; under InviteCode the invite stays
        // consumed (benign, swept later) — same as the previous TOCTOU behaviour.
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
    internal static async Task AntiTimingDelayAsync()
    {
#pragma warning disable CA5394, SCS0005
        var delayMs = Random.Shared.Next(100, 300);
#pragma warning restore CA5394, SCS0005
        await Task.Delay(delayMs);
    }
}
