using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authorization.AspNetCore;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Account;
using Modgud.Authentication.Api.Users;
using Modgud.Application.DTOs.User;
using Modgud.Application.Inbox;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;
using Wolverine;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin inbox for user-submitted change requests. Approval applies the request's
/// type-specific payload atomically; reject keeps the old values and records an
/// optional note. Each <see cref="ChangeRequestType"/> has its own apply handler.
/// </summary>
public static class AdminChangeRequestEndpoints
{
    /// <param name="ExpectedUpdatedAt">B4-AP-02 — the request's UpdatedAt as the reviewer saw it.
    /// When given and the request changed since, the approval is refused (409).</param>
    public record ApproveRequest(bool NotifyUser, DateTimeOffset? ExpectedUpdatedAt = null);
    public record RejectRequest(string? Note, bool NotifyUser);

    public static WebApplication MapAdminChangeRequestEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/change-requests")
            .WithTags("Admin Change Requests")
            .RequireAuthorization()
            // Approving/rejecting profile change requests is part of the
            // user-write surface — the same role that can edit users handles
            // these queue items.
            .RequiresPermission("user:write");

        group.MapGet("", async (IQuerySession session, bool includeTerminal = false) =>
        {
            var query = session.Query<UserChangeRequest>();
            var items = includeTerminal
                ? await query.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync()
                : await query.Where(r => r.Status == ChangeRequestStatus.AdminApprovalPending
                                      || r.Status == ChangeRequestStatus.EmailVerificationPending)
                             .OrderByDescending(r => r.RequestedAt).ToListAsync();

            var userIds = items.Select(r => r.UserId).Distinct().ToArray();
            var users = userIds.Length == 0
                ? new Dictionary<Guid, ApplicationUser>()
                : (await session.Query<ApplicationUser>()
                    .Where(u => u.Id.IsOneOf(userIds))
                    .ToListAsync()).ToDictionary(u => u.Id);

            return Results.Ok(items.Select(r =>
            {
                users.TryGetValue(r.UserId, out var u);
                return new
                {
                    Id = new ShortGuid(r.Id).ToString(),
                    UserId = new ShortGuid(r.UserId).ToString(),
                    UserLabel = u is not null
                        ? ($"{u.Firstname} {u.Lastname}".Trim() is { Length: > 0 } n ? $"{n} ({u.UserName})" : u.UserName)
                        : r.UserId.ToString(),
                    Type = r.Type.ToString(),
                    Status = r.Status.ToString(),
                    r.RequestedAt,
                    r.UpdatedAt,
                    r.VerifiedAt,
                    r.ReviewedAt,
                    r.ReviewerNote,
                    Changes = r.Type == ChangeRequestType.Profile
                        ? ProfileEndpoints.EnumerateProfileChanges(r.Payload, u)
                            .Select(c => new { c.Field, c.OldValue, c.NewValue })
                        : System.Linq.Enumerable.Empty<object>().Cast<dynamic>(),
                };
            }));
        });

        group.MapPost("{id}/approve", async (
            ShortGuid id,
            ApproveRequest request,
            IDocumentSession session,
            IMessageBus bus,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IInboxNotifier inboxNotifier,
            HttpContext context) =>
        {
            var cr = await session.LoadAsync<UserChangeRequest>(id.Guid);
            if (cr is null) return Results.NotFound();
            if (cr.Status != ChangeRequestStatus.AdminApprovalPending)
                return Results.BadRequest(new { Message = "Request is not awaiting admin approval" });
            // B4-AP-02 — approve what was reviewed: a request the user changed after the
            // reviewer opened it is not applied unseen. Stored timestamps carry microseconds.
            if (request?.ExpectedUpdatedAt is { } seen && Math.Abs((cr.UpdatedAt - seen).TotalMilliseconds) >= 1)
                return Results.Conflict(new
                {
                    Error = "ChangeRequest.Changed",
                    Message = "The request changed since you opened it. Reload and review it again.",
                });

            var user = await session.LoadAsync<ApplicationUser>(cr.UserId);
            if (user is null) return Results.Problem("Target user no longer exists", statusCode: 409);

            // B4-AP-01 — the apply marks the request Approved in the commit of the user change
            // (and of a follow-up access revoke), so there is no state where the change is in
            // effect but the request still waits, or the other way round.
            var approval = new ChangeRequestApproval(cr.Id, cr.UpdatedAt, context.GetUserId());
            var applyError = cr.Type switch
            {
                ChangeRequestType.Profile => await ApplyProfileAsync(cr.Payload, user, approval, bus),
                _ => Results.BadRequest(new { Message = $"Unknown request type: {cr.Type}" }),
            };
            if (applyError is not null) return applyError;
            cr.Status = ChangeRequestStatus.Approved;

            if (request?.NotifyUser == true && !string.IsNullOrWhiteSpace(user.Email))
            {
                var changes = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, user).ToList();
                await emailService.SendTemplatedEmailAsync(user.Email, EmailTemplate.ChangeRequestApproved,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                        ["Field"] = string.Join(", ", changes.Select(c => c.Field)),
                        ["NewValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.NewValue ?? "—"}")),
                    }), context.RequestAborted);
            }

            // Inbox: notify the requester (always, independent of email opt-in — inbox is
            // in-app, low-noise) and clear the "needs review" item from every admin's bell
            // via the ids recorded on the request at submit time.
            var changeList = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, user).ToList();
            await inboxNotifier.NotifyAsync(
                kind: InboxKind.ChangeRequestApproved,
                recipients: new[] { user.Id },
                titleKey: "inbox.kinds.changeRequestApproved.title",
                bodyKey: "inbox.kinds.changeRequestApproved.body",
                parameters: new
                {
                    FieldList = string.Join(", ", changeList.Select(c => c.Field)),
                    ChangeCount = changeList.Count,
                },
                link: "/profile",
                sourceType: ProfileEndpoints.ChangeRequestInboxSource,
                sourceId: cr.Id);
            if (cr.AdminInboxItemIds.Count > 0)
                await inboxNotifier.DismissByIdsAsync(cr.AdminInboxItemIds);

            Serilog.Log.Information("Admin: Change request approved. RequestId={RequestId} Type={Type}", cr.Id, cr.Type);
            return Results.Ok(new { Status = cr.Status.ToString() });
        });

        group.MapPost("{id}/reject", async (
            ShortGuid id,
            RejectRequest request,
            IDocumentSession session,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IInboxNotifier inboxNotifier,
            HttpContext context) =>
        {
            var cr = await session.LoadAsync<UserChangeRequest>(id.Guid);
            if (cr is null) return Results.NotFound();
            if (cr.Status is ChangeRequestStatus.Approved or ChangeRequestStatus.Rejected)
                return Results.BadRequest(new { Message = "Request is already in a terminal state" });

            cr.Status = ChangeRequestStatus.Rejected;
            cr.ReviewedAt = DateTimeOffset.UtcNow;
            cr.ReviewedByUserId = context.GetUserId();
            cr.ReviewerNote = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
            session.Store(cr);
            await session.SaveChangesAsync();

            var targetUser = await session.LoadAsync<ApplicationUser>(cr.UserId);
            if (request?.NotifyUser == true && targetUser is not null && !string.IsNullOrWhiteSpace(targetUser.Email))
            {
                var changes = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, targetUser).ToList();
                await emailService.SendTemplatedEmailAsync(targetUser.Email, EmailTemplate.ChangeRequestRejected,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = targetUser.Firstname ?? targetUser.UserName ?? "",
                        ["Field"] = string.Join(", ", changes.Select(c => c.Field)),
                        ["NewValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.NewValue ?? "—"}")),
                        ["ReviewerNote"] = cr.ReviewerNote ?? "—",
                    }), context.RequestAborted);
            }

            // Inbox: same shape as approval, plus the reviewer note so the user sees why.
            if (targetUser is not null)
            {
                var changeList = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, targetUser).ToList();
                await inboxNotifier.NotifyAsync(
                    kind: InboxKind.ChangeRequestRejected,
                    recipients: new[] { targetUser.Id },
                    titleKey: "inbox.kinds.changeRequestRejected.title",
                    bodyKey: "inbox.kinds.changeRequestRejected.body",
                    parameters: new
                    {
                        FieldList = string.Join(", ", changeList.Select(c => c.Field)),
                        ChangeCount = changeList.Count,
                        ReviewerNote = cr.ReviewerNote ?? string.Empty,
                    },
                    link: "/profile",
                    sourceType: ProfileEndpoints.ChangeRequestInboxSource,
                    sourceId: cr.Id);
            }
            if (cr.AdminInboxItemIds.Count > 0)
                await inboxNotifier.DismissByIdsAsync(cr.AdminInboxItemIds);

            Serilog.Log.Information("Admin: Change request rejected. RequestId={RequestId} Note={Note}",
                cr.Id, cr.ReviewerNote);
            return Results.Ok(new { Status = cr.Status.ToString() });
        });

        return application;
    }

    /// <summary>
    /// Applies the Profile payload by dispatching an <see cref="UpdateUserCommand"/>.
    /// Going via the command (rather than UserManager directly) is what appends
    /// UserUpdatedEvent — which drives the UserView async projection, the SignalR
    /// grid update, and the label-sync handlers for Todos/Comments. Without it, the
    /// admin grid stays stale and denormalized references never learn the new name.
    /// </summary>
    private static async Task<IResult?> ApplyProfileAsync(
        string payloadJson, ApplicationUser user, ChangeRequestApproval approval, IMessageBus bus)
    {
        var p = ProfileEndpoints.DeserializeProfile(payloadJson);

        var command = new UpdateUserCommand(
            UserId: user.Id,
            Firstname: p.Firstname,
            Lastname: p.Lastname,
            Acronym: p.Acronym.HasValue ? new Modgud.Domain.Common.Optional<string>(p.Acronym.Value ?? "") : default,
            Email: p.Email.HasValue ? new Modgud.Domain.Common.Optional<string>(p.Email.Value ?? "") : default,
            UserName: default,
            Approval: approval);

        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(command);
        if (!result.IsError) return null;
        if (result.FirstError is { Type: ErrorOr.ErrorType.Conflict, Code: "ChangeRequest.NotPending" or "ChangeRequest.Changed" } conflict)
            return Results.Conflict(new { Error = conflict.Code, Message = conflict.Description });
        return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
    }
}
