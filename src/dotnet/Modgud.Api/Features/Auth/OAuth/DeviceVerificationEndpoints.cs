using System.Security.Claims;
using Modgud.Api.Features.Auth.FunctionTerminals;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.FunctionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Device;
using Modgud.Domain.OAuth.Storage;
using Marten;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.OAuth;

/// <summary>
/// OAuth 2.0 Device Authorization Grant (RFC 8628) — the hosted end-user
/// verification step. The <c>connect/device</c> device-authorization endpoint
/// is handled internally by OpenIddict (it returns <c>device_code</c> /
/// <c>user_code</c> / <c>verification_uri</c>); the <c>device_code</c> token
/// exchange is handled in <see cref="AuthorizationEndpoints"/>. The piece this
/// file adds is the human-facing middle: the user opens the verification URI,
/// authenticates, sees what the device is asking for, and approves or denies.
///
/// <para>Design mirrors the consent flow (<see cref="ConsentEndpoints"/>):</para>
/// <list type="number">
///   <item><description><c>GET connect/verify</c> (OpenIddict passthrough):
///   authenticates the user (challenge → SPA login if needed), persists a
///   subject-bound <see cref="DeviceVerificationTicket"/> (capturing the
///   <c>user_code</c> from <c>verification_uri_complete</c> when present), and
///   redirects to the branded SPA page <c>/device?ticket=…</c>.</description></item>
///   <item><description><c>GET connect/device-verification?ticket=…</c> +
///   <c>POST …/code</c> (cookie-auth JSON) let the SPA resolve and display the
///   client + scopes for a user code.</description></item>
///   <item><description><c>POST connect/verify</c> (OpenIddict passthrough) is
///   the decision: on approve it <c>SignIn</c>s the user principal with the
///   OpenIddict scheme — OpenIddict binds it to the pending device code via the
///   <c>user_code</c>, so the polling device's <c>/connect/token</c> succeeds;
///   on deny it <c>Forbid</c>s with <c>access_denied</c>.</description></item>
/// </list>
/// </summary>
public static class DeviceVerificationEndpoints
{
    public static WebApplication MapDeviceVerificationEndpoints(this WebApplication app)
    {
        // OpenIddict end-user verification endpoint (passthrough). Handles its
        // own authentication (challenge → SPA login) like /connect/authorize —
        // so it is NOT behind RequireAuthorization.
        app.MapMethods("~/connect/verify", new[] { "GET", "POST" }, VerifyAsync)
            .WithName("OAuth_DeviceVerify")
            .WithTags("OpenIddict")
            .DisableAntiforgery();

        // SPA-facing read API for the /device page (cookie auth).
        var group = app.MapGroup("~/connect/device-verification")
            .WithTags("OpenIddict")
            .RequireAuthorization();

        group.MapGet("", GetDeviceInfoAsync).WithName("Device_Verify_Get");
        group.MapPost("code", SubmitCodeAsync).WithName("Device_Verify_SubmitCode");

        return app;
    }

    // ─── connect/verify (OpenIddict passthrough) ─────────────────────────

    private static async Task<IResult> VerifyAsync(
        HttpContext httpContext,
        IOpenIddictScopeManager scopeManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        UserManager<ApplicationUser> userManager,
        IDocumentSession session,
        AppSettings settings,
        IPermissionService permissionService)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Require an interactive session; bounce to the SPA login and back.
        var authResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!authResult.Succeeded)
        {
            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString,
                },
                new[] { IdentityConstants.ApplicationScheme });
        }

        var user = await userManager.GetUserAsync(authResult.Principal!);
        if (user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString,
                },
                new[] { IdentityConstants.ApplicationScheme });
        }

        if (!user.IsActive || user.IsDeleted)
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account has been deactivated.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        // POST = the user's approve/deny decision submitted from the /device page.
        if (HttpMethods.IsPost(httpContext.Request.Method))
        {
            var decision = (string?)request.GetParameter("decision");
            var approved = string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase);
            if (!approved)
            {
                return Results.Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user denied the device authorization request.",
                    }),
                    new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
            }

            // MG-FT-04 (plan §11.3/§11.4) — when the user code belongs to a
            // terminal-managed client, this approval is a terminal ENROLLMENT,
            // not a person consent: a different principal (the FUNCTION) gets
            // bound to the device code. Dispatch before the person path so a
            // terminal client can never mint a user-bound token.
            var normalizedUserCode = NormalizeUserCode(request.UserCode);
            var userCodeToken = normalizedUserCode.Length > 0
                ? await tokenManager.FindByReferenceIdAsync(normalizedUserCode, httpContext.RequestAborted)
                : null;
            var terminalTarget = userCodeToken is not null
                ? await LoadTerminalTargetAsync(
                    await tokenManager.GetApplicationIdAsync(userCodeToken, httpContext.RequestAborted),
                    session, httpContext.RequestAborted)
                : null;
            if (terminalTarget is not null)
            {
                return await ApproveTerminalEnrollmentAsync(
                    user, terminalTarget, userCodeToken!, normalizedUserCode,
                    settings, permissionService, session, tokenManager, httpContext.RequestAborted);
            }

            // OpenIddict matches request.UserCode → the pending device code and
            // attaches this principal to it. The verification request itself
            // carries no scopes, so resolve the scopes the device originally
            // requested (from the user code's authorization) and set them on the
            // principal — otherwise the issued token has no scopes and
            // offline_access is dropped (no refresh token).
            var scopeNames = await ResolveScopeNamesAsync(
                request.UserCode, tokenManager, authorizationManager, httpContext.RequestAborted);

            var principal = await AuthorizationEndpoints.CreateClaimsPrincipalAsync(
                user, request, scopeManager,
                scopeOverrides: scopeNames.Count > 0 ? scopeNames : null,
                userManager: userManager, cookiePrincipal: authResult.Principal);

            return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // GET = land here from the device's verification_uri[_complete]. Persist a
        // subject-bound ticket (capturing the user_code if the device embedded it)
        // and hand the SPA only the opaque ticket id.
        var ticket = new DeviceVerificationTicket
        {
            Id = Guid.CreateVersion7(),
            Subject = user.Id,
            UserCode = string.IsNullOrWhiteSpace(request.UserCode) ? null : request.UserCode,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        session.Store(ticket);
        await session.SaveChangesAsync();

        return Results.Redirect($"/device?ticket={ticket.Id:N}");
    }

    // ─── connect/device-verification (SPA read API) ──────────────────────

    private static async Task<IResult> GetDeviceInfoAsync(
        Guid ticket,
        AppSettings settings,
        IDocumentSession session,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal,
        CancellationToken cancellationToken)
    {
        var (record, error) = await ResolveTicketAsync(ticket, session, userManager, currentUserPrincipal);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(record!.UserCode))
        {
            return Results.Ok(new DeviceVerificationInfo { Ticket = record.Id.ToString("N"), Status = "needs_code" });
        }

        var resolved = await ResolveUserCodeAsync(
            record.UserCode!, settings, session, tokenManager, authorizationManager, applicationManager, scopeManager, cancellationToken);
        if (resolved is null)
        {
            // The code stored earlier no longer resolves (expired / already used).
            return Results.Ok(new DeviceVerificationInfo { Ticket = record.Id.ToString("N"), Status = "invalid_code" });
        }

        return Results.Ok(resolved with { Ticket = record.Id.ToString("N"), Status = "ready" });
    }

    private static async Task<IResult> SubmitCodeAsync(
        DeviceCodeSubmission submission,
        AppSettings settings,
        IDocumentSession session,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(submission.Ticket, "N", out var ticketId) &&
            !Guid.TryParse(submission.Ticket, out ticketId))
        {
            return Results.BadRequest(new { message = "Invalid ticket." });
        }

        var (record, error) = await ResolveTicketAsync(ticketId, session, userManager, currentUserPrincipal);
        if (error is not null) return error;

        var normalized = NormalizeUserCode(submission.UserCode);
        if (string.IsNullOrEmpty(normalized))
        {
            return Results.Ok(new DeviceVerificationInfo { Ticket = record!.Id.ToString("N"), Status = "invalid_code" });
        }

        var resolved = await ResolveUserCodeAsync(
            normalized, settings, session, tokenManager, authorizationManager, applicationManager, scopeManager, cancellationToken);
        if (resolved is null)
        {
            return Results.Ok(new DeviceVerificationInfo { Ticket = record!.Id.ToString("N"), Status = "invalid_code" });
        }

        record!.UserCode = normalized;
        session.Store(record);
        await session.SaveChangesAsync();

        return Results.Ok(resolved with { Ticket = record.Id.ToString("N"), Status = "ready" });
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>Resolves the OAuth client + requested scopes behind a device
    /// <c>user_code</c> for display. Returns null when the code doesn't resolve
    /// to a pending device authorization (unknown / expired / consumed).</summary>
    private static async Task<DeviceVerificationInfo?> ResolveUserCodeAsync(
        string userCode,
        AppSettings settings,
        IDocumentSession session,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        CancellationToken cancellationToken)
    {
        // OpenIddict normalizes the user code (strips formatting separators,
        // upper-cases) before hashing it into the reference id, so a raw
        // hyphenated/lower-cased code from verification_uri_complete won't match
        // a direct lookup — normalize the same way first.
        var token = await tokenManager.FindByReferenceIdAsync(NormalizeUserCode(userCode), cancellationToken);
        if (token is null) return null;

        var status = await tokenManager.GetStatusAsync(token, cancellationToken);
        if (!string.Equals(status, Statuses.Valid, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, Statuses.Inactive, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var clientId = await tokenManager.GetApplicationIdAsync(token, cancellationToken);
        string? clientName = null;
        if (!string.IsNullOrEmpty(clientId))
        {
            var application = await applicationManager.FindByIdAsync(clientId, cancellationToken);
            if (application is not null)
            {
                clientName = await applicationManager.GetDisplayNameAsync(application, cancellationToken)
                    ?? await applicationManager.GetClientIdAsync(application, cancellationToken);
            }
        }

        var scopeNames = await ResolveScopeNamesAsync(userCode, tokenManager, authorizationManager, cancellationToken);

        var scopeInfos = new List<DeviceScopeInfo>();
        foreach (var scopeName in scopeNames)
        {
            var scope = await scopeManager.FindByNameAsync(scopeName, cancellationToken);
            var displayName = scope is not null ? await scopeManager.GetDisplayNameAsync(scope, cancellationToken) : null;
            var description = scope is not null ? await scopeManager.GetDescriptionAsync(scope, cancellationToken) : null;
            scopeInfos.Add(new DeviceScopeInfo
            {
                Name = scopeName,
                DisplayName = displayName ?? scopeName,
                Description = description,
            });
        }

        // MG-FT-04 — a terminal-managed client gets the terminal consent
        // rendering: which function, which slot, which device key. The person
        // flow above stays byte-for-byte unchanged for everything else. When
        // the client IS terminal-managed but the flag is off or the link is
        // broken, the code reads as INVALID — never as a person consent, which
        // could otherwise mint a user-bound token for a terminal client.
        TerminalConsentInfo? terminalInfo = null;
        var target = await LoadTerminalTargetAsync(clientId, session, cancellationToken);
        if (target is not null)
        {
            if (!settings.Features.FunctionTerminals ||
                target.Terminal is null || target.Function is null || target.Function.IsDeleted)
            {
                return null;
            }

            var binding = await session.Query<DeviceCodeDpopBinding>()
                .Where(b => b.UserCodeHash == DeviceCodeDpopBindingKeyForVerification(userCode))
                .FirstOrDefaultAsync(cancellationToken);

            terminalInfo = new TerminalConsentInfo
            {
                FunctionName = target.Function.DisplayName,
                TerminalName = target.Terminal.DisplayName,
                Location = target.Terminal.Location,
                ClientId = target.Terminal.ClientId,
                DpopFingerprint = binding is { } b && b.ExpiresAt > DateTimeOffset.UtcNow
                    ? TerminalEnrollmentPrincipal.Fingerprint(b.Jkt)
                    : null,
            };
        }

        return new DeviceVerificationInfo
        {
            Ticket = string.Empty,
            Status = "ready",
            UserCode = NormalizeUserCode(userCode),
            ClientName = clientName,
            Scopes = scopeInfos,
            Kind = terminalInfo is null ? "user" : "terminal",
            Terminal = terminalInfo,
        };
    }

    /// <summary>Same normalization + hash the device-DPoP capture handler used
    /// for <c>UserCodeHash</c> (SHA-256 hex over the normalized user code).</summary>
    private static string DeviceCodeDpopBindingKeyForVerification(string userCode) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(NormalizeUserCode(userCode))));

    /// <summary>Resolution of a user code's OAuth client to its terminal
    /// enrollment target. Null = not a terminal-managed client → ordinary
    /// person flow. Non-null with null <see cref="Terminal"/>/<see cref="Function"/>
    /// = the client claims a terminal link that doesn't resolve — callers must
    /// REFUSE, never fall back to the person flow.</summary>
    private sealed record TerminalVerificationTarget(
        TerminalEnrollment? Terminal,
        FunctionPrincipal? Function);

    private static async Task<TerminalVerificationTarget?> LoadTerminalTargetAsync(
        string? applicationId,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(applicationId, out var appId)) return null;

        var state = await session.LoadAsync<OAuthApplicationState>(appId, cancellationToken);
        if (state is null || state.IsDeleted || state.ManagedTerminalEnrollmentId is not { } terminalId)
            return null;

        // §11.4 rule 4 — the client must belong to exactly this enrollment,
        // verified in BOTH directions.
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, cancellationToken);
        if (terminal is null || terminal.OAuthApplicationId != appId)
            return new TerminalVerificationTarget(null, null);

        var function = await session.LoadAsync<FunctionPrincipal>(terminal.FunctionPrincipalId, cancellationToken);
        return new TerminalVerificationTarget(terminal, function);
    }

    /// <summary>The terminal-enrollment approval (plan §11.4). Every check
    /// failure is an <c>access_denied</c> Forbid — OpenIddict rejects the
    /// device authorization, the device's poll fails, and the terminal needs a
    /// fresh device-flow attempt. Check 1 (the admin holds an interactive,
    /// cookie-authenticated session) is enforced by the caller before the POST
    /// branch is ever reached; a dedicated step-up/re-auth prompt is a
    /// hardening follow-up, not part of MG-FT-04.</summary>
    private static async Task<IResult> ApproveTerminalEnrollmentAsync(
        ApplicationUser admin,
        TerminalVerificationTarget target,
        object userCodeToken,
        string normalizedUserCode,
        AppSettings settings,
        IPermissionService permissionService,
        IDocumentSession session,
        IOpenIddictTokenManager tokenManager,
        CancellationToken cancellationToken)
    {
        static IResult Refuse(string description) => Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });

        // Feature gate — a terminal client whose flag was switched off after
        // creation must not be enrollable (and never falls through to the
        // person flow; the caller dispatched here on the client link alone).
        if (!settings.Features.FunctionTerminals)
            return Refuse("Function terminals are not enabled.");

        // Check 4 — client ↔ terminal link intact (both directions).
        if (target.Terminal is null || target.Function is null)
            return Refuse("The OAuth client is not linked to a valid terminal slot.");

        // Check 2 — the approving admin holds the enrollment permission.
        if (!await permissionService.HasPermissionAsync(admin.Id, AppSlugs.Modgud, "function-terminal:enroll"))
            return Refuse("You are not authorized to enroll function terminals.");

        // Check 3 — only a Pending slot is enrollable; re-enrollment of an
        // Active/Disabled/Revoked slot is always a fresh slot instead.
        if (target.Terminal.Status != TerminalEnrollmentStatus.Pending)
            return Refuse("The terminal slot is not pending enrollment.");

        // Check 5 — function alive; check 6 — terminal use enabled on it.
        if (target.Function.IsDeleted)
            return Refuse("The function no longer exists.");
        if (!target.Function.TerminalPolicy.Enabled)
            return Refuse("Terminal use is disabled for this function.");

        // Check 7 — the initial device request must have been DPoP-proofed:
        // without a bound key there is nothing to pin the enrollment to.
        var binding = await session.Query<DeviceCodeDpopBinding>()
            .Where(b => b.UserCodeHash == DeviceCodeDpopBindingKeyForVerification(normalizedUserCode))
            .FirstOrDefaultAsync(cancellationToken);
        if (binding is null || binding.ExpiresAt <= DateTimeOffset.UtcNow)
            return Refuse("The device request was not DPoP-bound; terminal enrollment requires a device key.");

        // Check 8 — the user code still resolves to a redeemable device grant.
        var status = await tokenManager.GetStatusAsync(userCodeToken, cancellationToken);
        if (!string.Equals(status, Statuses.Valid, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, Statuses.Inactive, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("The user code is no longer valid.");
        }

        // Audit record of the approval — written as already consumed: the
        // decision IS the consumption (single POST), the row exists so "who
        // approved which slot when" survives independently of OpenIddict's
        // token table.
        var now = DateTimeOffset.UtcNow;
        session.Store(new TerminalEnrollmentVerificationTicket
        {
            Id = Guid.CreateVersion7(),
            ApprovingAdminUserId = admin.Id,
            TerminalEnrollmentId = target.Terminal.Id,
            UserCode = normalizedUserCode,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
            ConsumedAt = now,
        });
        await session.SaveChangesAsync(cancellationToken);

        // OpenIddict binds this FUNCTION principal to the pending device code;
        // the terminal's poll then reaches the token-endpoint enrollment
        // exchange (§11.6) with token_use=terminal_enrollment.
        return Results.SignIn(
            TerminalEnrollmentPrincipal.Create(target.Function, target.Terminal),
            properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Resolves the scope names the device originally requested, via the
    /// user code's authorization. Empty when the code doesn't resolve.</summary>
    private static async Task<IReadOnlyList<string>> ResolveScopeNamesAsync(
        string? userCode,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userCode)) return Array.Empty<string>();

        var token = await tokenManager.FindByReferenceIdAsync(NormalizeUserCode(userCode), cancellationToken);
        if (token is null) return Array.Empty<string>();

        var authorizationId = await tokenManager.GetAuthorizationIdAsync(token, cancellationToken);
        if (string.IsNullOrEmpty(authorizationId)) return Array.Empty<string>();

        var authorization = await authorizationManager.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null) return Array.Empty<string>();

        return (await authorizationManager.GetScopesAsync(authorization, cancellationToken)).ToArray();
    }

    /// <summary>RFC 8628 user codes are case-insensitive and commonly shown with
    /// formatting separators for readability (e.g. <c>WDJB-MJHT</c>). Mirror
    /// OpenIddict's normalization: keep only letters/digits and upper-case, so a
    /// code typed/pasted with hyphens or spaces still resolves.</summary>
    private static string NormalizeUserCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        Span<char> buffer = stackalloc char[raw.Length];
        var n = 0;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
        }
        return new string(buffer[..n]);
    }

    private static async Task<(DeviceVerificationTicket? Record, IResult? Error)> ResolveTicketAsync(
        Guid ticketId,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal)
    {
        var record = await session.LoadAsync<DeviceVerificationTicket>(ticketId);
        if (record is null)
        {
            return (null, Results.NotFound(new { message = "Device verification ticket not found or expired." }));
        }

        if (record.ConsumedAt is not null)
        {
            return (null, Results.Conflict(new { message = "Device verification ticket has already been used." }));
        }

        if (record.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return (null, Results.BadRequest(new { message = "Device verification ticket has expired." }));
        }

        // Subject binding — only the user whose session created the ticket may act on it.
        var user = await userManager.GetUserAsync(currentUserPrincipal);
        if (user is null || user.Id != record.Subject)
        {
            return (null, Results.Forbid());
        }

        return (record, null);
    }
}

public record DeviceVerificationInfo
{
    public required string Ticket { get; init; }

    /// <summary><c>needs_code</c> = no user_code yet, prompt for entry;
    /// <c>ready</c> = client + scopes resolved, show approve/deny;
    /// <c>invalid_code</c> = the entered code didn't resolve.</summary>
    public required string Status { get; init; }

    /// <summary>The normalized user code, echoed back so the SPA can submit it
    /// to <c>POST connect/verify</c> for the approve/deny decision. Only set
    /// when <see cref="Status"/> is <c>ready</c>.</summary>
    public string? UserCode { get; init; }

    public string? ClientName { get; init; }
    public List<DeviceScopeInfo> Scopes { get; init; } = new();

    /// <summary><c>user</c> = the ordinary person device flow; <c>terminal</c>
    /// = a terminal-managed client asking for enrollment (MG-FT-04) — the SPA
    /// renders the terminal consent instead of the scope consent.</summary>
    public string Kind { get; init; } = "user";

    /// <summary>Set when <see cref="Kind"/> is <c>terminal</c>.</summary>
    public TerminalConsentInfo? Terminal { get; init; }
}

/// <summary>What the approving admin must see before registering a device as a
/// terminal (plan §11.4): which function, which slot, and the key fingerprint
/// of the device asking.</summary>
public record TerminalConsentInfo
{
    public required string FunctionName { get; init; }
    public required string TerminalName { get; init; }
    public string? Location { get; init; }
    public required string ClientId { get; init; }
    /// <summary>Null when the device request carried no DPoP proof — approval
    /// is refused in that case (rule 7).</summary>
    public string? DpopFingerprint { get; init; }
}

public record DeviceScopeInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
}

public class DeviceCodeSubmission
{
    public required string Ticket { get; init; }
    public required string UserCode { get; init; }
}
