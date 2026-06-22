using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// ADR-0011 — explicit native passwordless REGISTRATION endpoint. The companion
/// to JIT-on-OTP (<see cref="NativeOtpEndpoints"/>): where the
/// <see cref="SelfRegPosture.JitOnOtp"/> posture turns the OTP-request endpoint
/// into sign-in-or-sign-up, the <see cref="SelfRegPosture.ExplicitEndpoint"/>
/// posture keeps sign-in strict (the OTP endpoint serves only known users) and
/// routes sign-up through this deliberate step — room for an app to gate
/// registration behind its own ToS / profile UI.
///
/// <para>It fires ONLY when the resolved Application posture is
/// <see cref="SelfRegPosture.ExplicitEndpoint"/> AND native grants are enabled
/// (the emailed code is a native OTP, redeemed at <c>/connect/token</c> via
/// <c>grant_type=urn:cocoar:otp</c>, which also flips <c>EmailConfirmed</c>).
/// Every other case — wrong posture, native grants off, or an existing
/// confirmed user — returns the same uniform response with the same anti-timing
/// jitter and the same per-IP SMTP rate limit as the OTP-request endpoint, so it
/// is not an email-existence oracle.</para>
/// </summary>
public static class NativeRegisterEndpoints
{
    public record NativeRegisterRequestDto(string Email, string? FirstName = null, string? LastName = null);

    public static WebApplication MapNativeRegisterEndpoints(this WebApplication application, string path)
    {
        // POST /api/account/native/register — explicit passwordless sign-up.
        application.MapPost($"{path}/account/native/register", async (
            NativeRegisterRequestDto request,
            HttpContext httpContext,
            IDocumentSession session,
            IApplicationSettingsResolver settingsResolver,
            IEmailOtpService emailOtpService,
            IPasswordlessUserFactory passwordlessUserFactory,
            CancellationToken ct) =>
        {
            const string genericMessage = "If registration is available, you will receive a verification code.";

            // Host-time resolution: the begin request carries no client_id, so the
            // App (if any) comes from the request Host (an Application subdomain).
            var settings = await settingsResolver.ResolveForRequestAsync(httpContext, clientId: null, ct);

            // Two gates, both required: the (App⊕realm) NativeGrants master flag
            // (the code is a native OTP redeemed at /connect/token) AND the
            // ExplicitEndpoint posture. Under Off/JitOnOtp this endpoint does
            // nothing — JIT sign-up flows through the OTP-request endpoint, and
            // Off has no self-registration at all.
            var eligible = settings.NativeGrants is { Enabled: true }
                           && settings.SelfRegPosture == SelfRegPosture.ExplicitEndpoint;

            // Required-field gate (configurable per App⊕realm). Surfaced as a hard
            // 400 BEFORE the uniform branch and only when the endpoint is eligible to
            // act — both are email-independent, so this leaks nothing. Username is
            // never enforced here (the native username is always the email).
            if (eligible && RegistrationFieldsPolicy.FirstMissingRequiredName(
                    settings.RegistrationFields, request.FirstName, request.LastName) is { } missing)
            {
                return Results.BadRequest(new { error = $"{missing} is required." });
            }

            if (eligible && !string.IsNullOrWhiteSpace(request.Email))
            {
                // Anti-enumeration: every branch is silent (results discarded) so
                // the uniform response + jitter below is the only observable.
                await IssueRegistrationOtpAsync(
                    request.Email, request.FirstName, request.LastName,
                    session, emailOtpService, passwordlessUserFactory, ct);
            }

            // Same jitter on every branch (incl. the success path, which did real
            // work) so response time carries no signal about whether the email exists.
            await AntiTimingDelayAsync();
            return Results.Ok(new { Message = genericMessage });
        })
        .WithTags("Native Auth")
        .AllowAnonymous()
        .WithName("NativeRegister_Request")
        // Same per-IP SMTP cap class as the OTP-request / magic-link endpoints.
        .RequireRateLimiting("native-otp");

        return application;
    }

    /// <summary>
    /// Explicit-registration routing (ADR-0011). All outcomes are silent so the
    /// caller's uniform response + jitter is the only observable.
    /// <list type="bullet">
    ///   <item>Unknown email → create a passwordless user and issue the
    ///   registration code.</item>
    ///   <item>Known passwordless still-unconfirmed user → re-issue the code
    ///   (resend for an in-progress sign-up).</item>
    ///   <item>Otherwise (an already-confirmed user, or a password-bearing
    ///   unconfirmed account) → nothing. Sign-in (the OTP endpoint) and the web
    ///   verification flow own those.</item>
    /// </list>
    /// </summary>
    private static async Task IssueRegistrationOtpAsync(
        string email,
        string? firstName,
        string? lastName,
        IDocumentSession session,
        IEmailOtpService emailOtpService,
        IPasswordlessUserFactory passwordlessUserFactory,
        CancellationToken ct)
    {
        var user = await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant() && !u.IsDeleted, ct);

        if (user is null)
        {
            var created = await passwordlessUserFactory.CreateAsync(email, firstName, lastName, ct);
            if (created is not null)
                _ = await emailOtpService.RequestNativeRegistrationOtpAsync(created.Id, ct);
            return;
        }

        // A passwordless, still-unconfirmed sign-up may resend its code. A
        // confirmed user is already registered (→ sign in), and a password-bearing
        // unconfirmed account must verify via the web link — neither is served here.
        if (!user.EmailConfirmed && string.IsNullOrEmpty(user.PasswordHash))
            _ = await emailOtpService.RequestNativeRegistrationOtpAsync(user.Id, ct);
    }

    /// <summary>
    /// Random delay (100-300ms) to mask whether an email exists. Non-security
    /// jitter — Random.Shared is correct here (the real OTP secret uses CSPRNG
    /// in EmailOtpService). Mirrors <see cref="NativeOtpEndpoints"/>.
    /// </summary>
    private static async Task AntiTimingDelayAsync()
    {
#pragma warning disable CA5394, SCS0005
        var delayMs = Random.Shared.Next(100, 300);
#pragma warning restore CA5394, SCS0005
        await Task.Delay(delayMs);
    }
}
