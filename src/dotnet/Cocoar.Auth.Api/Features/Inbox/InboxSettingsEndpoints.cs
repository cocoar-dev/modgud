using Microsoft.AspNetCore.Mvc;
using Marten;
using Cocoar.Auth.Application.Inbox;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Api.Features.Inbox;

/// <summary>
/// Admin CRUD for the singleton <see cref="InboxRetentionSettings"/> document.
/// The doc lives at a fixed id; first read returns the C# defaults if no row
/// exists yet. PUT replaces the document wholesale — admin always submits the
/// full settings shape.
/// </summary>
public static class InboxSettingsEndpoints
{
    public static WebApplication MapInboxSettingsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/inbox-settings")
            .WithTags("Admin / Inbox Settings")
            .RequireAuthorization();

        group.MapGet("", async (IQuerySession session, CancellationToken ct) =>
        {
            var settings = await session.LoadAsync<InboxRetentionSettings>(InboxRetentionSettings.SingletonId, ct)
                ?? new InboxRetentionSettings();
            return Results.Ok(settings);
        })
            .WithName("V2_AdminInboxSettings_Get")
            .RequiresPermission("inbox-settings:read");

        group.MapPut("", async ([FromBody] InboxRetentionSettings body, IDocumentSession session, CancellationToken ct) =>
        {
            // Force the singleton id regardless of what the client sent —
            // guards against accidental multi-row writes.
            body.Id = InboxRetentionSettings.SingletonId;
            body.UpdatedAt = DateTime.UtcNow;
            session.Store(body);
            await session.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .WithName("V2_AdminInboxSettings_Update")
            .RequiresPermission("inbox-settings:write");

        return app;
    }
}
