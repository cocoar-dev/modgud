using BuildingBlocks.Helper;
using Marten;
using Cocoar.Auth.Application.Inbox;
using Cocoar.Auth.Application.Inbox.Events;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Cocoar.Auth.Api.Features.Inbox;

/// <summary>
/// User-facing inbox surface. Every endpoint scopes by the calling user's
/// id — no admin override for "see all inboxes" (an admin can navigate the
/// underlying projection in Marten directly if they really need to).
/// </summary>
public static class InboxEndpoints
{
    public record InboxCountDto(int Total, int Unread);

    public record SnoozeRequest(DateTime? Until);

    public static WebApplication MapInboxEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/inbox")
            .WithTags("Inbox")
            .RequireAuthorization();

        // List items for the current user. Filter knobs:
        //  - includeRead=true (default): include items the user has read
        //  - includeDismissed=false (default): hide dismissed items
        //  - kind=AdminChangeRequestSubmitted: filter by kind
        //  - take=N (default 200): cap result size — the inbox panel doesn't
        //    need everything ever, only the most recent.
        group.MapGet("", async (
            HttpContext ctx,
            IQuerySession session,
            string? kind,
            bool includeRead = true,
            bool includeDismissed = false,
            int take = 200) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = session.Query<InboxItemView>()
                .Where(i => i.RecipientUserId == userId.Value);

            if (!includeDismissed) q = q.Where(i => i.DismissedAt == null);
            if (!includeRead) q = q.Where(i => i.ReadAt == null);
            if (!string.IsNullOrEmpty(kind) &&
                Enum.TryParse<InboxKind>(kind, out var k))
            {
                q = q.Where(i => i.Kind == k);
            }

            var views = await q.OrderByDescending(i => i.CreatedAt).Take(take).ToListAsync();
            return Results.Ok(views.Select(v => v.ToDto()));
        })
        .WithName("V2_Inbox_List");

        group.MapGet("count", async (HttpContext ctx, IQuerySession session) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var open = session.Query<InboxItemView>()
                .Where(i => i.RecipientUserId == userId.Value && i.DismissedAt == null);

            var total = await open.CountAsync();
            var unread = await open.Where(i => i.ReadAt == null).CountAsync();

            return Results.Ok(new InboxCountDto(total, unread));
        })
        .WithName("V2_Inbox_Count");

        group.MapPost("{id}/read", async (
            ShortGuid id,
            HttpContext ctx,
            IDocumentSession session) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var view = await session.LoadAsync<InboxItemView>(id.Guid);
            if (view is null || view.RecipientUserId != userId.Value)
                return Results.NotFound();

            if (view.ReadAt is not null)
                return Results.NoContent(); // idempotent

            session.Events.Append(id.Guid, new InboxItemReadEvent(id.Guid, DateTime.UtcNow));
            await session.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("V2_Inbox_Read");

        group.MapPost("read-all", async (HttpContext ctx, IDocumentSession session) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var ids = await session.Query<InboxItemView>()
                .Where(i => i.RecipientUserId == userId.Value && i.ReadAt == null && i.DismissedAt == null)
                .Select(i => i.Id)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var id in ids)
            {
                session.Events.Append(id, new InboxItemReadEvent(id, now));
            }
            await session.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("V2_Inbox_ReadAll");

        group.MapPost("{id}/dismiss", async (
            ShortGuid id,
            HttpContext ctx,
            IDocumentSession session) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var view = await session.LoadAsync<InboxItemView>(id.Guid);
            if (view is null || view.RecipientUserId != userId.Value)
                return Results.NotFound();

            if (view.DismissedAt is not null)
                return Results.NoContent();

            session.Events.Append(id.Guid, new InboxItemDismissedEvent(id.Guid, DateTime.UtcNow));
            await session.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("V2_Inbox_Dismiss");

        group.MapPost("dismiss-all", async (HttpContext ctx, IDocumentSession session) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var ids = await session.Query<InboxItemView>()
                .Where(i => i.RecipientUserId == userId.Value && i.DismissedAt == null)
                .Select(i => i.Id)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var id in ids)
            {
                session.Events.Append(id, new InboxItemDismissedEvent(id, now));
            }
            await session.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("V2_Inbox_DismissAll");

        group.MapPost("{id}/snooze", async (
            ShortGuid id,
            HttpContext ctx,
            IDocumentSession session,
            SnoozeRequest body) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var view = await session.LoadAsync<InboxItemView>(id.Guid);
            if (view is null || view.RecipientUserId != userId.Value)
                return Results.NotFound();

            var descriptor = InboxKindRegistry.Get(view.Kind);
            if (!descriptor.Actionable)
                return Results.BadRequest(new { error = "Kind is not actionable (snooze not supported)" });

            session.Events.Append(id.Guid, new InboxItemSnoozedEvent(id.Guid, body.Until));
            await session.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("V2_Inbox_Snooze");

        // Static descriptor catalog for the frontend — one fetch on app load,
        // lets the client know icons + persistence per kind without
        // hard-coding the registry on the SPA side.
        group.MapGet("kinds", () =>
        {
            return Results.Ok(InboxKindRegistry.All.Select(d => new
            {
                Kind = d.Kind.ToString(),
                Persistence = d.Persistence.ToString(),
                Actionable = d.Actionable,
                Severity = d.Severity.ToString(),
                Icon = d.Icon,
                I18nPrefix = d.I18nPrefix,
            }));
        })
        .WithName("V2_Inbox_Kinds");

        return app;
    }
}
