using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Identity;
using TimeToDo.Authorization.AspNetCore;
using TimeToDo.Authentication.ExtensionMethods;
using TimeToDo.Authentication.Api.Account;
using TimeToDo.Authentication.Api.Users;
using TimeToDo.Application.DTOs.User;
using TimeToDo.Authentication.Domain;
using TimeToDo.Infrastructure.Email;
using Wolverine;

namespace TimeToDo.Authentication.Api.Admin;

/// <summary>
/// Admin inbox for user-submitted change requests. Approval applies the request's
/// type-specific payload atomically; reject keeps the old values and records an
/// optional note. Each <see cref="ChangeRequestType"/> has its own apply handler.
/// </summary>
public static class AdminChangeRequestEndpoints
{
    public record ApproveRequest(bool NotifyUser);
    public record RejectRequest(string? Note, bool NotifyUser);

    public static WebApplication MapAdminChangeRequestEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/change-requests")
            .WithTags("Admin Change Requests")
            .RequireAuthorization()
            .RequiresPermission("app:admin");

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
            HttpContext context) =>
        {
            var cr = await session.LoadAsync<UserChangeRequest>(id.Guid);
            if (cr is null) return Results.NotFound();
            if (cr.Status != ChangeRequestStatus.AdminApprovalPending)
                return Results.BadRequest(new { Message = "Request is not awaiting admin approval" });

            var user = await session.LoadAsync<ApplicationUser>(cr.UserId);
            if (user is null) return Results.Problem("Target user no longer exists", statusCode: 409);

            var applyError = cr.Type switch
            {
                ChangeRequestType.Profile => await ApplyProfileAsync(cr.Payload, user, bus),
                _ => Results.BadRequest(new { Message = $"Unknown request type: {cr.Type}" }),
            };
            if (applyError is not null) return applyError;

            cr.Status = ChangeRequestStatus.Approved;
            cr.ReviewedAt = DateTimeOffset.UtcNow;
            cr.ReviewedByUserId = context.GetUserId();
            session.Store(cr);
            await session.SaveChangesAsync();

            if (request?.NotifyUser == true && !string.IsNullOrWhiteSpace(user.Email))
            {
                var changes = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, user).ToList();
                await emailService.SendTemplatedEmailAsync(user.Email, EmailTemplate.ChangeRequestApproved,
                    new Dictionary<string, string>
                    {
                        ["AppName"] = "TimeToDo",
                        ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                        ["Field"] = string.Join(", ", changes.Select(c => c.Field)),
                        ["NewValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.NewValue ?? "—"}")),
                    });
            }

            Serilog.Log.Information("Admin: Change request approved. RequestId={RequestId} Type={Type}", cr.Id, cr.Type);
            return Results.Ok(new { Status = cr.Status.ToString() });
        });

        group.MapPost("{id}/reject", async (
            ShortGuid id,
            RejectRequest request,
            IDocumentSession session,
            IEmailService emailService,
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

            if (request?.NotifyUser == true)
            {
                var user = await session.LoadAsync<ApplicationUser>(cr.UserId);
                if (user is not null && !string.IsNullOrWhiteSpace(user.Email))
                {
                    var changes = ProfileEndpoints.EnumerateProfileChanges(cr.Payload, user).ToList();
                    await emailService.SendTemplatedEmailAsync(user.Email, EmailTemplate.ChangeRequestRejected,
                        new Dictionary<string, string>
                        {
                            ["AppName"] = "TimeToDo",
                            ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                            ["Field"] = string.Join(", ", changes.Select(c => c.Field)),
                            ["NewValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.NewValue ?? "—"}")),
                            ["ReviewerNote"] = cr.ReviewerNote ?? "—",
                        });
                }
            }

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
        string payloadJson, ApplicationUser user, IMessageBus bus)
    {
        var p = ProfileEndpoints.DeserializeProfile(payloadJson);

        var command = new UpdateUserCommand(
            UserId: user.Id,
            Firstname: p.Firstname,
            Lastname: p.Lastname,
            Acronym: p.Acronym.HasValue ? new TimeToDo.Domain.Common.Optional<string>(p.Acronym.Value ?? "") : default,
            Email: p.Email.HasValue ? new TimeToDo.Domain.Common.Optional<string>(p.Email.Value ?? "") : default,
            UserName: default);

        var result = await bus.InvokeAsync<ErrorOr.ErrorOr<UserDto>>(command);
        return result.IsError
            ? Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) })
            : null;
    }
}
