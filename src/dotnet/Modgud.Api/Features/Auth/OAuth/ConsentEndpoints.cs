using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Modgud.Authentication.Domain;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Consent;
using Modgud.Infrastructure.OpenIddict.Cimd;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.OAuth;

/// <summary>
/// Consent UI flow — server-side-ticket variant.
///
/// <para>
/// Replaces the legacy "round-trip the raw authorize URL through the SPA"
/// design that combined three security issues — OAUTH-02 (scope expansion
/// via decision payload), OAUTH-03 (no subject binding → consent-on-behalf
/// CSRF), OAUTH-08 (open-redirect via reflected returnUrl). The new shape:
/// </para>
///
/// <list type="number">
///   <item><description><c>/connect/authorize</c> creates a
///   <see cref="ConsentTicket"/> bound to the current user, with
///   <c>ClientId</c> + <c>RequestedScopes</c> + the original authorize
///   query locked in. Redirects to <c>/consent?ticket={id}</c>.</description></item>
///   <item><description><c>GET /connect/consent?ticket=…</c> resolves the
///   ticket, verifies subject + expiry + not-already-used, returns the
///   info the SPA needs to render the prompt.</description></item>
///   <item><description><c>POST /connect/consent</c> takes the ticket id
///   plus the user's <c>ApprovedScopes</c>. The server intersects with the
///   locked-in <c>RequestedScopes</c> (no expansion possible), creates
///   the persistent authorization, marks the ticket consumed, and
///   reconstructs the redirect URL from the locked-in query string —
///   the SPA never sees the OAuth URL.</description></item>
/// </list>
/// </summary>
public static class ConsentEndpoints
{
    public static WebApplication MapConsentEndpoints(this WebApplication app, string pathBase = "connect")
    {
        var group = app.MapGroup($"~/{pathBase}/consent")
            .WithTags("OpenIddict")
            .RequireAuthorization();

        group.MapGet("", GetConsentInfoAsync).WithName("OAuth_Consent_Get");
        group.MapPost("", SubmitConsentAsync).WithName("OAuth_Consent_Post");

        return app;
    }

    private static async Task<IResult> GetConsentInfoAsync(
        Guid ticket,
        IDocumentSession session,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<ApplicationUser> userManager,
        CimdClientResolver cimdResolver,
        ClaimsPrincipal currentUserPrincipal,
        CancellationToken cancellationToken)
    {
        var (record, error) = await ResolveTicketAsync(ticket, session, userManager, currentUserPrincipal);
        if (error is not null) return error;

        var application = await applicationManager.FindByClientIdAsync(record!.ClientId);
        if (application is null) return Results.NotFound(new { message = "Application not found." });

        var clientName = await applicationManager.GetDisplayNameAsync(application) ?? record.ClientId;

        // Surface IsDynamicallyRegistered so ConsentView.vue can render the
        // [unverified] marker for DCR clients. Loading the projection state
        // is faster than going through the OpenIddict application manager
        // for one property and stays in the tenant-scoped session.
        var state = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == record.ClientId && !x.IsDeleted, cancellationToken);

        // CIMD clients are non-persisted, so the direct query misses them —
        // fall back to the resolver. The synthesized state carries
        // DcrIsDynamicallyRegistered=true, so a CIMD client shows the same
        // [unverified] treatment as a DCR client.
        state ??= await cimdResolver.ResolveAsync(record.ClientId, cancellationToken);

        // For a CIMD client the client_id IS an https URL — surface its
        // hostname so the user verifies the real domain that owns this app
        // (phishing mitigation), independent of the self-asserted
        // display name.
        string? clientIdHostname = null;
        if (CimdClientId.IsCimdClientId(record.ClientId)
            && Uri.TryCreate(record.ClientId, UriKind.Absolute, out var clientIdUri))
        {
            clientIdHostname = clientIdUri.Host;
        }
        // Marten/Newtonsoft roundtrip: booleans may come back as either
        // a JsonElement (System.Text.Json path) or a plain bool
        // (Newtonsoft auto-conversion); handle both.
        var isDcr = state is not null
            && state.Properties.TryGetValue(OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered, out var raw)
            && raw switch
            {
                bool b => b,
                JsonElement el when el.ValueKind is JsonValueKind.True => true,
                _ => false,
            };

        var scopeInfos = new List<ConsentScopeInfo>();
        foreach (var scopeName in record.RequestedScopes)
        {
            var scope = await scopeManager.FindByNameAsync(scopeName);
            string? displayName = null;
            string? description = null;
            if (scope is not null)
            {
                displayName = await scopeManager.GetDisplayNameAsync(scope);
                description = await scopeManager.GetDescriptionAsync(scope);
            }
            scopeInfos.Add(new ConsentScopeInfo
            {
                Name = scopeName,
                DisplayName = displayName ?? scopeName,
                Description = description,
                Required = scopeName == Scopes.OpenId,
            });
        }

        return Results.Ok(new ConsentModel
        {
            Ticket = record.Id.ToString("N"),
            ClientId = record.ClientId,
            ClientName = clientName,
            RequestedScopes = scopeInfos,
            ExpiresAt = record.ExpiresAt,
            IsDynamicallyRegistered = isDcr,
            ClientIdHostname = clientIdHostname,
        });
    }

    private static async Task<IResult> SubmitConsentAsync(
        ConsentDecision decision,
        IDocumentSession session,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal)
    {
        if (!Guid.TryParseExact(decision.Ticket, "N", out var ticketId) &&
            !Guid.TryParse(decision.Ticket, out ticketId))
        {
            return Results.BadRequest(new { message = "Invalid consent ticket." });
        }

        var (record, error) = await ResolveTicketAsync(ticketId, session, userManager, currentUserPrincipal);
        if (error is not null) return error;

        // Mark the ticket consumed BEFORE doing anything else so two parallel
        // POSTs (e.g. user double-click) cannot both succeed. SaveChangesAsync
        // below atomically commits this together with the authorization.
        record!.ConsumedAt = DateTimeOffset.UtcNow;

        if (!decision.Approved)
        {
            session.Store(record);
            await session.SaveChangesAsync();
            return Results.Ok(new ConsentResult
            {
                RedirectUrl = $"/consent/denied?error={Uri.EscapeDataString(Errors.AccessDenied)}" +
                              $"&error_description={Uri.EscapeDataString("The user denied the authorization request.")}",
            });
        }

        // OAUTH-02 fix: the user-submitted ApprovedScopes are filtered against
        // the requested scopes that were locked in at /authorize time. Anything
        // the user "added" beyond what the RP asked for is silently dropped;
        // the standard "openid is implicit" semantic is preserved.
        var requestedSet = record.RequestedScopes.ToHashSet(StringComparer.Ordinal);
        var approvedSet = decision.ApprovedScopes
            .Where(s => requestedSet.Contains(s))
            .ToHashSet(StringComparer.Ordinal);

        if (requestedSet.Contains(Scopes.OpenId))
        {
            approvedSet.Add(Scopes.OpenId);
        }

        var application = await applicationManager.FindByClientIdAsync(record.ClientId);
        if (application is null) return Results.NotFound(new { message = "Application not found." });

        var user = await userManager.GetUserAsync(currentUserPrincipal);
        if (user is null) return Results.Unauthorized();

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(approvedSet);

        await authorizationManager.CreateAsync(
            principal: principal,
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application) ?? string.Empty,
            type: AuthorizationTypes.Permanent,
            scopes: approvedSet.ToImmutableArray());

        session.Store(record);
        await session.SaveChangesAsync();

        // OAUTH-08 fix: reconstruct the redirect from the SERVER-SIDE locked
        // query string. The SPA never sees the OAuth URL — there's no chance
        // for it to get tampered with between consent display and submit.
        return Results.Ok(new ConsentResult
        {
            RedirectUrl = "/connect/authorize" + record.AuthorizeRequestQuery,
        });
    }

    /// <summary>
    /// Loads a ticket and validates that it's safe to act on:
    /// exists, not expired, not already consumed, and bound to the
    /// authenticated principal. Returns either the record or an
    /// <see cref="IResult"/> describing the failure.
    /// </summary>
    private static async Task<(ConsentTicket? Record, IResult? Error)> ResolveTicketAsync(
        Guid ticketId,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal)
    {
        var record = await session.LoadAsync<ConsentTicket>(ticketId);
        if (record is null)
        {
            return (null, Results.NotFound(new { message = "Consent ticket not found or expired." }));
        }

        if (record.ConsumedAt is not null)
        {
            return (null, Results.Conflict(new { message = "Consent ticket has already been used." }));
        }

        if (record.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return (null, Results.BadRequest(new { message = "Consent ticket has expired." }));
        }

        // OAUTH-03 fix: subject binding. An attacker forcing a victim to POST
        // a consent decision can only act on tickets the victim's own session
        // created — and tickets are only created by /authorize, which is
        // session-scoped. Cross-user tampering is impossible by construction.
        var user = await userManager.GetUserAsync(currentUserPrincipal);
        if (user is null || user.Id != record.Subject)
        {
            return (null, Results.Forbid());
        }

        return (record, null);
    }
}

public class ConsentModel
{
    public required string Ticket { get; init; }
    public required string ClientId { get; init; }
    public required string ClientName { get; init; }
    public required List<ConsentScopeInfo> RequestedScopes { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>True for clients minted via RFC 7591 DCR or resolved via a
    /// CIMD <c>client_id</c> URL; lets the consent UI render an
    /// <c>[unverified]</c> marker + warning text so the user pauses before
    /// authorising a self-onboarded client.</summary>
    public bool IsDynamicallyRegistered { get; init; }

    /// <summary>For a CIMD client, the hostname of the <c>client_id</c> URL
    /// (e.g. <c>claude.ai</c>) — the domain that owns the metadata document.
    /// Null for DCR / admin-registered clients. The consent UI shows it
    /// prominently as a phishing mitigation: the user verifies the real
    /// domain, not just the self-asserted display name.</summary>
    public string? ClientIdHostname { get; init; }
}

public class ConsentScopeInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}

public class ConsentDecision
{
    public bool Approved { get; init; }
    public List<string> ApprovedScopes { get; init; } = new();

    /// <summary>
    /// Server-side ticket id (32-char hex, accepts hyphenated form too).
    /// Replaces the legacy <c>ReturnUrl</c> field.
    /// </summary>
    public required string Ticket { get; init; }
}

public class ConsentResult
{
    public required string RedirectUrl { get; init; }
}
