using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Json.Mutable;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Serilog;
using Modgud.Domain.Common;
using Modgud.Application.Inbox;
using Modgud.Authentication;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Realms;
using Modgud.Authentication.Identity;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// User-facing self-service profile changes. Every edit flows through an aggregate
/// <see cref="UserChangeRequest"/> gated by (optional) email verification and admin
/// approval — never a direct edit. The request stores the payload as opaque JSON so
/// the domain stays type-free; this endpoint owns serialization/deserialization for
/// <see cref="ChangeRequestType.Profile"/>.
/// </summary>
public static class ProfileEndpoints
{
    private const int VerificationTokenLifetimeHours = 24;
    private const int VerificationTokenBytes = 32;

    /// <summary>Request body from the SPA. Every form field comes through on every submit;
    /// null means "leave pending value as-is" — server merges non-null fields into the
    /// Optional-typed <see cref="ProfileUpdateDto"/> stored on the change request.</summary>
    public record ProfileChangeRequestDto(string? Firstname, string? Lastname, string? Acronym, string? Email);
    public record VerifyEmailChangeDto(string RequestId, string Token);

    // Shared JSON options — the Optional-aware resolver + converter make the serialized
    // payload use property presence as the HasValue indicator (no "{HasValue, Value}" wrapping).
    internal static readonly JsonSerializerOptions PayloadJsonOptions = CreatePayloadOptions();

    private static JsonSerializerOptions CreatePayloadOptions()
    {
        var o = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            PropertyNamingPolicy = null,
        };
        o.AddOptionalAware();
        return o;
    }

    public static WebApplication MapProfileEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/account/profile")
            .WithTags("Account Profile");

        group.MapPut("request", [Authorize] async (
            ProfileChangeRequestDto body,
            UserManager<ApplicationUser> userManager,
            IDocumentSession session,
            IRealmProvisioningService realmSvc,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IAdminNotifier adminNotifier,
            IInboxNotifier inboxNotifier,
            IWebHostEnvironment env,
            HttpContext context,
            CancellationToken ct) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            // Gate the whole change-request flow on a verified email. The
            // workflow round-trips through the user's mailbox (email-verify
            // step + admin-approval notification), so an unverified address
            // would leave the request stranded. Frontend disables the Save
            // button parallel to this; the 403 here is the safety net.
            if (!user.EmailConfirmed)
                return Results.Json(new { error = "Email not verified", code = "Account.EmailNotVerified" }, statusCode: 403);

            string? desiredEmail = null;
            var emailSubmitted = body.Email is not null;
            if (emailSubmitted)
            {
                var trimmed = body.Email!.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('@'))
                    return Results.BadRequest(new { Message = "Invalid email address" });
                desiredEmail = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            }

            if (emailSubmitted && !string.IsNullOrEmpty(desiredEmail))
            {
                var clash = await userManager.FindByEmailAsync(desiredEmail!);
                if (clash is not null && clash.Id != user.Id)
                    return Results.Conflict(new { Message = "Email already in use" });
            }

            var existing = (await session.Query<UserChangeRequest>()
                .Where(r => r.UserId == user.Id && r.Type == ChangeRequestType.Profile
                         && (r.Status == ChangeRequestStatus.EmailVerificationPending
                          || r.Status == ChangeRequestStatus.AdminApprovalPending))
                .ToListAsync()).FirstOrDefault();

            // Track whether the request was *already* in AdminApprovalPending — if so,
            // re-submitting an edit shouldn't re-notify admins (they already have an item
            // for this request via the inbox dedup; the live update is silent).
            var wasInAdminPending = existing?.Status == ChangeRequestStatus.AdminApprovalPending;

            // Snapshot the previous pending email (if any) before we merge — we need it
            // to decide whether the verify token has to be regenerated.
            var previousPending = existing is not null
                ? DeserializeProfile(existing.Payload)
                : new ProfileUpdateDto();
            var previousPendingEmail = previousPending.Email;
            var existingTokenHash = existing?.VerificationTokenHash;

            // Build the "submission" DTO with Optional.HasValue = true on every field the
            // user actually submitted. Optional.None fields are omitted from the JSON via
            // ShouldSerialize, so merging is a clean "overlay only what the user touched".
            var submitted = new ProfileUpdateDto();
            if (body.Firstname is not null) submitted.Firstname = body.Firstname.Trim();
            if (body.Lastname is not null)  submitted.Lastname  = body.Lastname.Trim();
            if (body.Acronym is not null)   submitted.Acronym   = new Optional<string?>(NormalizeOptional(body.Acronym));
            if (emailSubmitted)             submitted.Email     = new Optional<string?>(desiredEmail);

            // Deep-merge submitted over existing payload. Flat property overwrite would
            // break once payloads grow nested objects (e.g. future Phone = { Country, Number })
            // because the whole nested object would get replaced instead of merged.
            var mergedPayload = MergeJson(existing?.Payload ?? "{}", submitted);

            // Strip no-op entries — a pending value that equals the current user value is
            // not actually a request (e.g. user reverted the form back to their current
            // name). If nothing meaningful is left, drop the request entirely.
            var (cleanedPayload, hasAnyPending) = CleanupProfilePayload(mergedPayload, user);
            if (!hasAnyPending)
            {
                if (existing is not null)
                {
                    session.Delete(existing);
                    await session.SaveChangesAsync();
                }
                return Results.Ok(new { Open = (object?)null });
            }

            var request = existing ?? new UserChangeRequest
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Type = ChangeRequestType.Profile,
                RequestedAt = DateTimeOffset.UtcNow,
            };
            request.Payload = cleanedPayload;
            request.UpdatedAt = DateTimeOffset.UtcNow;

            // Re-read the cleaned payload once to drive status + email-verify logic.
            var pending = DeserializeProfile(cleanedPayload);

            // Token regen logic — triggers when email is pending but differs from the last
            // value we sent a link to (or no token exists yet).
            string? rawToken = null;
            if (!pending.Email.HasValue)
            {
                request.VerificationTokenHash = null;
                request.VerificationExpiresAt = null;
                request.VerifiedAt = null;
            }
            else if (!previousPendingEmail.HasValue
                  || previousPendingEmail.Value != pending.Email.Value
                  || existingTokenHash is null)
            {
                rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(VerificationTokenBytes))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                request.VerificationTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
                request.VerificationExpiresAt = DateTimeOffset.UtcNow.AddHours(VerificationTokenLifetimeHours);
                request.VerifiedAt = null;
            }

            request.Status = pending.Email.HasValue && request.VerifiedAt is null
                ? ChangeRequestStatus.EmailVerificationPending
                : ChangeRequestStatus.AdminApprovalPending;

            session.Store(request);
            await session.SaveChangesAsync();

            var realm = await context.ResolveCurrentRealmAsync(realmSvc, ct);
            if (realm is not null && rawToken is not null && pending.Email.HasValue && !string.IsNullOrEmpty(pending.Email.Value))
            {
                var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
                var verifyUrl = $"{appUrl}/verify-email?id={new ShortGuid(request.Id)}&token={Uri.EscapeDataString(rawToken)}";
                await emailService.SendTemplatedEmailAsync(pending.Email.Value!, EmailTemplate.EmailVerification,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                        ["ActionUrl"] = verifyUrl,
                        ["ExpirationHours"] = VerificationTokenLifetimeHours.ToString(),
                    }, ct: ct), ct);
            }

            // Inbox notification: fires only when this submit *transitions* the request
            // into AdminApprovalPending. Repeated edits of an already-pending request are
            // silently merged so admins don't get spammed. Record the created item-ids on
            // the request so approve/reject/withdraw can dismiss them deterministically.
            if (!wasInAdminPending && request.Status == ChangeRequestStatus.AdminApprovalPending)
            {
                var ids = await NotifyAdminsInboxAsync(adminNotifier, inboxNotifier, request, user);
                if (ids.Count > 0)
                {
                    request.AdminInboxItemIds = ids.ToList();
                    session.Store(request);
                    await session.SaveChangesAsync();
                }
            }

            Log.Information("Profile: Change request upserted. UserId={UserId} Status={Status}",
                user.Id, request.Status);

            return Results.Ok(new { Open = MapForApi(request, user) });
        });

        group.MapPost("request/verify-email", async (
            VerifyEmailChangeDto body,
            IDocumentSession session,
            IEmailService emailService,
            Modgud.Authentication.Applications.IEmailBrandingResolver emailBranding,
            IAdminNotifier adminNotifier,
            IInboxNotifier inboxNotifier,
            IRealmProvisioningService realmSvc,
            IWebHostEnvironment env,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!ShortGuid.TryParse(body.RequestId, out Guid requestGuid))
                return Results.BadRequest(new { Message = "Invalid request id" });

            var cr = await session.LoadAsync<UserChangeRequest>(requestGuid);
            if (cr is null || cr.Status != ChangeRequestStatus.EmailVerificationPending)
                return Results.BadRequest(new { Message = "Request not found or already processed" });

            if (cr.VerificationExpiresAt is null || cr.VerificationExpiresAt < DateTimeOffset.UtcNow)
                return Results.BadRequest(new { Message = "Verification link expired" });

            var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.Token)));
            if (!string.Equals(cr.VerificationTokenHash, tokenHash, StringComparison.Ordinal))
                return Results.BadRequest(new { Message = "Invalid token" });

            cr.Status = ChangeRequestStatus.AdminApprovalPending;
            cr.VerifiedAt = DateTimeOffset.UtcNow;
            cr.UpdatedAt = DateTimeOffset.UtcNow;
            cr.VerificationTokenHash = null; // single-use
            session.Store(cr);
            await session.SaveChangesAsync();

            var user = await session.LoadAsync<ApplicationUser>(cr.UserId);
            var recipients = await adminNotifier.GetAdminRecipientsAsync();
            var realm = await context.ResolveCurrentRealmAsync(realmSvc, ct);
            if (recipients.Count > 0 && user is not null && realm is not null)
            {
                var appUrl = RealmPublicUrl.RealmPublicBaseUrl(realm, env);
                var changes = EnumerateProfileChanges(cr.Payload, user).ToList();
                await emailService.SendTemplatedEmailAsync(recipients,
                    EmailTemplate.AdminChangeRequestNotification,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["RequestingUser"] = $"{user.Firstname} {user.Lastname} ({user.UserName})".Trim(),
                        ["Field"] = string.Join(", ", changes.Select(c => c.Field)),
                        ["OldValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.OldValue ?? "—"}")),
                        ["NewValue"] = string.Join(" · ", changes.Select(c => $"{c.Field}: {c.NewValue ?? "—"}")),
                        ["ActionUrl"] = $"{appUrl}/admin/change-requests",
                    }, ct: ct), ct);
            }

            // Inbox: notify admins that this request is now ready for review. The
            // submit path may have already notified (covered the verified-from-start
            // case); this one covers the email-verify-then-pending path. Same dedup
            // by sourceId means a duplicate fire would coalesce — but we still record
            // the ids so withdraw/approve/reject can dismiss across recipients.
            if (user is not null)
            {
                var ids = await NotifyAdminsInboxAsync(adminNotifier, inboxNotifier, cr, user);
                if (ids.Count > 0)
                {
                    cr.AdminInboxItemIds = ids.ToList();
                    session.Store(cr);
                    await session.SaveChangesAsync();
                }
            }

            Log.Information("Profile: Email verified, awaiting admin approval. RequestId={RequestId}", cr.Id);
            return Results.Ok(new { Status = cr.Status.ToString() });
        })
        .AllowAnonymous();

        group.MapDelete("request", [Authorize] async (
            UserManager<ApplicationUser> userManager,
            IDocumentSession session,
            IInboxNotifier inboxNotifier,
            HttpContext context) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            var existing = (await session.Query<UserChangeRequest>()
                .Where(r => r.UserId == user.Id && r.Type == ChangeRequestType.Profile
                         && (r.Status == ChangeRequestStatus.EmailVerificationPending
                          || r.Status == ChangeRequestStatus.AdminApprovalPending))
                .ToListAsync()).FirstOrDefault();
            if (existing is null) return Results.NoContent();

            // Snapshot the inbox-item ids BEFORE deleting the request — we still need
            // to dismiss every admin's bell entry after the cr itself is gone.
            var adminItemIds = existing.AdminInboxItemIds.ToList();

            session.Delete(existing);
            await session.SaveChangesAsync();

            if (adminItemIds.Count > 0)
                await inboxNotifier.DismissByIdsAsync(adminItemIds);

            Log.Information("Profile: Change request cancelled. UserId={UserId}", user.Id);
            return Results.NoContent();
        });

        group.MapGet("request", [Authorize] async (
            UserManager<ApplicationUser> userManager,
            IQuerySession session,
            HttpContext context) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();

            var all = await session.Query<UserChangeRequest>()
                .Where(r => r.UserId == user.Id && r.Type == ChangeRequestType.Profile)
                .OrderByDescending(r => r.RequestedAt)
                .Take(20)
                .ToListAsync();

            var open = all.FirstOrDefault(r => r.Status == ChangeRequestStatus.EmailVerificationPending
                                            || r.Status == ChangeRequestStatus.AdminApprovalPending);
            var lastTerminal = all.FirstOrDefault(r => r.Status == ChangeRequestStatus.Approved
                                                    || r.Status == ChangeRequestStatus.Rejected);

            return Results.Ok(new
            {
                Open = open is null ? null : MapForApi(open, user),
                LastTerminal = lastTerminal is null ? null : MapForApi(lastTerminal, user),
            });
        });

        return application;
    }

    // ── helpers shared with Admin endpoints ──

    /// <summary>
    /// Whitespace-only inputs collapse to null. Non-empty strings are trimmed.
    /// Used at the request boundary to normalise raw form values before they
    /// reach the merge — guards against "        " on an Optional&lt;string&gt;
    /// field reaching the payload as a non-empty-but-blank value.
    /// </summary>
    internal static string? NormalizeOptional(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim();

    /// <summary>
    /// Deep-merges a typed submission DTO onto an existing JSON payload string using
    /// <see cref="MutableJsonMerge"/>. Submitted properties (Optional.HasValue = true)
    /// override the existing values; fields not submitted keep whatever was pending.
    /// Recursive for nested objects — a future payload like
    /// <c>Phone: { Country, Number }</c> only touches the sub-keys it was handed.
    /// </summary>
    internal static string MergeJson(string existingJson, object submission)
    {
        var submittedBytes = JsonSerializer.SerializeToUtf8Bytes(submission, PayloadJsonOptions);
        var existing = (MutableJsonObject)MutableJsonDocument.Parse(Encoding.UTF8.GetBytes(existingJson));
        var submitted = (MutableJsonObject)MutableJsonDocument.Parse(submittedBytes);
        MutableJsonMerge.MergeDestructive(existing, submitted);
        return Encoding.UTF8.GetString(MutableJsonDocument.ToUtf8Bytes(existing));
    }

    /// <summary>Deserializes a Profile-type payload. Returns a fresh DTO if the JSON is
    /// empty or unparseable so callers don't need to null-check.</summary>
    public static ProfileUpdateDto DeserializeProfile(string payloadJson)
        => string.IsNullOrWhiteSpace(payloadJson)
            ? new ProfileUpdateDto()
            : JsonSerializer.Deserialize<ProfileUpdateDto>(payloadJson, PayloadJsonOptions) ?? new ProfileUpdateDto();

    /// <summary>Emits (Field, OldValue, NewValue) entries for every Optional that
    /// <see cref="Optional{T}.HasValue"/> = true. OldValue is the caller-supplied user
    /// value. The payload is expected to have been cleaned of no-op entries at submission
    /// time; we don't double-filter here.</summary>
    internal static IEnumerable<(string Field, string? OldValue, string? NewValue)> EnumerateProfileChanges(
        string payloadJson, ApplicationUser? user)
    {
        var p = DeserializeProfile(payloadJson);
        if (p.Firstname.HasValue) yield return ("Firstname", user?.Firstname, p.Firstname.Value);
        if (p.Lastname.HasValue)  yield return ("Lastname",  user?.Lastname,  p.Lastname.Value);
        if (p.Acronym.HasValue)   yield return ("Acronym",   user?.Acronym,   p.Acronym.Value);
        if (p.Email.HasValue)     yield return ("Email",     user?.Email,     p.Email.Value);
    }

    /// <summary>Drops payload entries whose value matches the user's current profile —
    /// they would be no-op approvals and shouldn't clutter the pending request.</summary>
    internal static (string Json, bool HasAny) CleanupProfilePayload(string json, ApplicationUser user)
    {
        var p = DeserializeProfile(json);
        if (p.Firstname.HasValue && StringEq(p.Firstname.Value, user.Firstname)) p.Firstname = Optional<string>.None;
        if (p.Lastname.HasValue  && StringEq(p.Lastname.Value,  user.Lastname))  p.Lastname  = Optional<string>.None;
        if (p.Acronym.HasValue   && StringEq(p.Acronym.Value,   user.Acronym))   p.Acronym   = Optional<string?>.None;
        if (p.Email.HasValue     && StringEq(p.Email.Value,     user.Email))     p.Email     = Optional<string?>.None;

        var hasAny = p.Firstname.HasValue || p.Lastname.HasValue || p.Acronym.HasValue || p.Email.HasValue;
        return (JsonSerializer.Serialize(p, PayloadJsonOptions), hasAny);
    }

    /// <summary>
    /// Treats null and empty string as equivalent (case-sensitive otherwise).
    /// Used to compare submitted profile fields against the user's current
    /// values — Identity often hands back <c>""</c> for unset fields and the
    /// SPA may send <c>null</c> on the same submission.
    /// </summary>
    internal static bool StringEq(string? a, string? b)
    {
        var na = string.IsNullOrEmpty(a) ? null : a;
        var nb = string.IsNullOrEmpty(b) ? null : b;
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    internal static object MapForApi(UserChangeRequest r, ApplicationUser? user) => new
    {
        Id = new ShortGuid(r.Id).ToString(),
        Type = r.Type.ToString(),
        Status = r.Status.ToString(),
        r.RequestedAt,
        r.UpdatedAt,
        r.VerifiedAt,
        r.ReviewedAt,
        r.ReviewerNote,
        Changes = r.Type == ChangeRequestType.Profile
            ? EnumerateProfileChanges(r.Payload, user).Select(c => new { c.Field, c.OldValue, c.NewValue })
            : Enumerable.Empty<object>().Cast<dynamic>(),
    };

    /// <summary>Marker stored on inbox items so cross-cutting code (retention, dismiss
    /// sweeps) can find every notification that references a change-request.</summary>
    internal const string ChangeRequestInboxSource = "change-request";

    /// <summary>
    /// Build and send the admin-side inbox notification for a change-request that just
    /// transitioned into <see cref="ChangeRequestStatus.AdminApprovalPending"/>. The
    /// item dedups by <c>(change-request, request.Id)</c> — repeated transitions on
    /// the same request collapse onto the same bell entry. The returned inbox-item ids
    /// are stored on <c>cr.AdminInboxItemIds</c> by the caller so a later approve /
    /// reject / withdraw can dismiss them deterministically.
    /// </summary>
    internal static async Task<IReadOnlyList<Guid>> NotifyAdminsInboxAsync(
        IAdminNotifier adminNotifier,
        IInboxNotifier inboxNotifier,
        UserChangeRequest cr,
        ApplicationUser user,
        CancellationToken ct = default)
    {
        var adminUserIds = await adminNotifier.GetAdminRecipientUserIdsAsync(ct);
        if (adminUserIds.Count == 0) return [];

        var changes = EnumerateProfileChanges(cr.Payload, user).ToList();
        var userLabel = $"{user.Firstname} {user.Lastname} ({user.UserName})".Trim();
        if (string.IsNullOrWhiteSpace(userLabel)) userLabel = user.UserName ?? "—";

        return await inboxNotifier.NotifyAsync(
            kind: InboxKind.AdminChangeRequestSubmitted,
            recipients: adminUserIds,
            titleKey: "inbox.kinds.adminChangeRequestSubmitted.title",
            bodyKey: "inbox.kinds.adminChangeRequestSubmitted.body",
            parameters: new
            {
                UserLabel = userLabel,
                FieldList = string.Join(", ", changes.Select(c => c.Field)),
                ChangeCount = changes.Count,
            },
            link: "/admin/change-requests",
            sourceType: ChangeRequestInboxSource,
            sourceId: cr.Id,
            ct: ct);
    }
}
