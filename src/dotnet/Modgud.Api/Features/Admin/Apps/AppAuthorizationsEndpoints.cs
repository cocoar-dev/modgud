using System.Security.Claims;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Api.Features.Management;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.OpenIddict;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Admin.Apps;

/// <summary>
/// The OAuth authorizations (one per user × client grant) that reach an
/// Application's resource servers, and the revocation of a single one.
///
/// <para>Built for the consuming application's own backend: it shows its users
/// their connected systems, keys its per-connection state on the token's
/// <c>sub</c> + <c>oi_au_id</c>, and needs "disconnect" to end the refresh
/// token in Modgud too — not merely to stop honouring the access token on its
/// side. The connected system is the OAuth client here, so RFC 7009 token
/// revocation is not available to the resource server.</para>
///
/// <para>An authorization is in an App's reach when one of its scopes is
/// app-scoped to that App, or lists one of the App's OAuth APIs as a resource.
/// Anything else answers 404 — a caller learns nothing about grants that only
/// concern other Apps.</para>
/// </summary>
public static class AppAuthorizationsEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;
    private const int ScanBatch = 500;

    public static WebApplication MapAppAuthorizationsEndpoints(this WebApplication application, string path)
    {
        // Mapped outside the cookie-only app group for the same reason as
        // GET /app/{id}/scope: both are part of the OAuth Management API.
        application.MapGet($"{path}/app/{{id}}/authorizations", ListAsync)
            .WithTags("Apps")
            .WithName("V2_App_GetAuthorizations")
            .RequiresManagementPermission("oauth-authorization:read", clientAppRouteParameter: "id");

        application.MapDelete($"{path}/app/{{id}}/authorizations/{{authorizationId}}", RevokeAsync)
            .WithTags("Apps")
            .WithName("V2_App_RevokeAuthorization")
            .RequiresManagementPermission("oauth-authorization:revoke", clientAppRouteParameter: "id");

        return application;
    }

    private static async Task<IResult> ListAsync(
        ShortGuid id,
        string? subject,
        string? after,
        int? limit,
        IDocumentSession session,
        CancellationToken ct)
    {
        var reach = await AppReach.LoadAsync(session, id.Guid, ct);
        if (reach is null) return Results.NotFound();

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = new List<OpenIddictAuthorizationDocument>(take);
        var cursor = after;
        var exhausted = false;

        // The scope→App match can't be pushed into the query (it is a set
        // intersection over a JSON array), so scan valid authorizations in id
        // order and filter in memory until the page is full.
        while (!exhausted && items.Count < take)
        {
            var query = session.Query<OpenIddictAuthorizationDocument>()
                .Where(x => x.Status == Statuses.Valid);
            if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
            if (!string.IsNullOrEmpty(cursor)) query = query.Where(x => x.Id.CompareTo(cursor) > 0);

            var batch = await query.OrderBy(x => x.Id).Take(ScanBatch).ToListAsync(ct);

            var consumed = 0;
            foreach (var row in batch)
            {
                consumed++;
                cursor = row.Id;
                if (reach.Covers(row)) items.Add(row);
                if (items.Count == take) break;
            }

            // Done only when the store had no further rows AND none of this
            // batch was left unread by a full page.
            exhausted = batch.Count < ScanBatch && consumed == batch.Count;
        }

        var clientIds = await ResolveClientIdsAsync(session, items, ct);
        return Results.Ok(new
        {
            Items = items.Select(x => ToDto(x, clientIds)),
            NextAfter = exhausted ? null : cursor,
        });
    }

    private static async Task<IResult> RevokeAsync(
        ShortGuid id,
        string authorizationId,
        HttpContext http,
        IDocumentSession session,
        IOAuthGrantRevoker grants,
        IClientSessionService clientSessions,
        IPrincipalLookupService principals,
        ISecurityAuditLog audit,
        CancellationToken ct)
    {
        var reach = await AppReach.LoadAsync(session, id.Guid, ct);
        if (reach is null) return Results.NotFound();

        var row = await session.LoadAsync<OpenIddictAuthorizationDocument>(authorizationId, ct);
        if (row is null || !reach.Covers(row)) return Results.NotFound();

        // Idempotent: a repeated disconnect is a success, not an error. Tokens
        // go first, so an attempt that died halfway leaves the authorization
        // valid and the retry is audited as the revoke it is. The token count
        // can't carry that signal — TryRevokeAsync also reports success for a
        // token that was revoked already.
        var wasValid = string.Equals(row.Status, Statuses.Valid, StringComparison.Ordinal);
        var tokensRevoked = await grants.RevokeTokensByAuthorizationIdAsync(row.Id, ct);
        await grants.RevokeAuthorizationByIdAsync(row.Id, ct);
        await clientSessions.EndByAuthorizationAsync(row.Id, ct);

        if (wasValid)
        {
            var clientIds = await ResolveClientIdsAsync(session, [row], ct);
            var actorId = Guid.TryParse(
                http.User.GetClaim(Claims.Subject) ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier),
                out var parsedActor)
                ? parsedActor
                : (Guid?)null;
            var actor = actorId is { } a ? await principals.GetByIdAsync(a, ct) : null;

            await audit.RecordRequiredAsync(new SecurityAuditRecord
            {
                EventType = AuditEvents.AuthorizationRevoked,
                ActorKind = actor is ServiceAccount ? AuditActorKind.ServiceAccount : AuditActorKind.User,
                ActorSubjectId = actorId,
                TargetSubjectId = Guid.TryParse(row.Subject, out var target) ? target : null,
                OAuthClientId = row.ApplicationId is { } appPk ? clientIds.GetValueOrDefault(appPk) : null,
                AuthorizationId = row.Id,
                ApplicationId = id.Guid,
                OutcomeCode = AuditOutcomes.Succeeded,
                OperationCode = "revoke-authorization",
                Count = tokensRevoked,
            }, ct);
        }

        return Results.NoContent();
    }

    private static async Task<Dictionary<string, string>> ResolveClientIdsAsync(
        IDocumentSession session,
        IReadOnlyCollection<OpenIddictAuthorizationDocument> rows,
        CancellationToken ct)
    {
        // CIMD clients are never persisted, so their authorizations resolve to
        // no client row — the DTO then carries a null ClientId.
        var pks = rows
            .Select(x => Guid.TryParse(x.ApplicationId, out var pk) ? pk : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (pks.Count == 0) return new Dictionary<string, string>();

        var clients = await session.Query<OAuthApplicationState>()
            .Where(x => pks.Contains(x.Id))
            .ToListAsync(ct);
        return clients.ToDictionary(x => x.Id.ToString(), x => x.ClientId, StringComparer.OrdinalIgnoreCase);
    }

    // Id and Subject are returned exactly as they appear in the access token
    // (`oi_au_id`, `sub`) — not as ShortGuids — because the consumer joins on them.
    private static object ToDto(OpenIddictAuthorizationDocument x, Dictionary<string, string> clientIds) => new
    {
        x.Id,
        x.Subject,
        ClientId = x.ApplicationId is { } pk ? clientIds.GetValueOrDefault(pk) : null,
        x.Type,
        Scopes = x.Scopes.OrderBy(s => s, StringComparer.Ordinal),
        CreatedAt = x.CreationDate,
    };

    private sealed class AppReach
    {
        private readonly HashSet<string> _scopeNames;

        private AppReach(HashSet<string> scopeNames) => _scopeNames = scopeNames;

        public bool Covers(OpenIddictAuthorizationDocument row) => _scopeNames.Overlaps(row.Scopes);

        public static async Task<AppReach?> LoadAsync(IDocumentSession session, Guid appId, CancellationToken ct)
        {
            var app = await session.LoadAsync<App>(appId, ct);
            if (app is null) return null;

            var apiNames = (await session.Query<OAuthApiState>()
                    .Where(x => !x.IsDeleted && x.AppId == appId)
                    .Select(x => x.Name)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var scopes = await session.Query<OAuthScopeState>()
                .Where(x => !x.IsDeleted)
                .ToListAsync(ct);

            return new AppReach(scopes
                .Where(s => s.AppId == appId || s.Resources.Any(apiNames.Contains))
                .Select(s => s.Name)
                .ToHashSet(StringComparer.Ordinal));
        }
    }
}
