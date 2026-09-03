using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Modgud.Authentication.Gdpr;

/// <summary>
/// GDPR self-service: export-data (Article 20), request-deletion / confirm /
/// cancel, plus the admin-facing permanent-erase. Uses Marten's
/// <c>ApplyEventDataMasking</c> to scrub PII from existing event streams,
/// then <c>ArchiveStream</c> to remove the user's stream from regular
/// queries. Also deletes secondary documents (sessions, security data,
/// external identity links) and revokes the user's live access (OAuth grants
/// + sessions + security stamp) via <see cref="IUserAccessRevoker"/>.
/// </summary>
public class GdprService(
    IDocumentStore store,
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    IPermissionService permissionService,
    IEmailService emailService,
    IUserAccessRevoker accessRevoker,
    IRealmSettingsService realmSettings,
    IHttpContextAccessor httpContextAccessor) : IGdprService
{
    public async Task<ErrorOr<UserDataExportDto>> ExportUserDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("User.NotFound", $"User {userId} not found.");

        // GDPR exports the user's permissions in the IDP itself — the
        // realm-wide admin surface they hold. External app permissions are
        // not part of the IDP's data export today.
        var permissions = await permissionService.GetUserPermissionsAsync(userId, AppSlugs.Modgud, ct);
        var sessions = await session.Query<UserSession>()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
        var clientSessions = await session.Query<ClientSession>()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);

        var loginHistory = await GetLoginHistoryAsync(userId, 100, ct);

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Export);

        return new UserDataExportDto
        {
            Metadata = new ExportMetadataDto
            {
                ExportedAt = DateTimeOffset.UtcNow,
                FormatVersion = "1.1",
                UserId = userId,
            },
            Profile = new ExportProfileDto
            {
                UserName = user.UserName ?? string.Empty,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Firstname = user.Firstname,
                Lastname = user.Lastname,
                Acronym = user.Acronym,
                IsActive = user.IsActive,
            },
            Security = new ExportSecurityDto
            {
                TwoFactorEnabled = user.TwoFactorEnabled,
                EmailOtpEnabled = user.EmailOtpEnabled,
                LockoutEnabled = user.LockoutEnabled,
                LockoutEnd = user.LockoutEnd,
                AccessFailedCount = user.AccessFailedCount,
            },
            Permissions = permissions,
            Sessions = sessions.Select(s => new ExportSessionDto
            {
                Kind = "Browser",
                IpAddress = s.IpAddress,
                Browser = s.Browser,
                OperatingSystem = s.OperatingSystem,
                DeviceType = s.DeviceType,
                CreatedAt = s.CreatedAt,
                LastActiveAt = s.LastActiveAt,
                ExpiresAt = s.ExpiresAt,
                AbsoluteExpiresAt = s.AbsoluteExpiresAt,
            }).Concat(clientSessions.Select(s => new ExportSessionDto
            {
                Kind = "OAuthClient",
                ClientId = s.ClientId,
                ClientDisplayName = s.ClientDisplayName,
                IpAddress = s.IpAddress,
                Browser = s.Browser,
                OperatingSystem = s.OperatingSystem,
                DeviceType = s.DeviceType,
                CreatedAt = s.CreatedAt,
                LastActiveAt = s.LastActiveAt,
                ExpiresAt = s.ExpiresAt,
                AbsoluteExpiresAt = s.AbsoluteExpiresAt,
            })).ToList(),
            LoginHistory = loginHistory,
        };
    }

    public async Task<ErrorOr<DeletionRequestResponseDto>> RequestDeletionAsync(Guid userId, string password, string? reason, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("User.NotFound", $"User {userId} not found.");

        if (!await userManager.CheckPasswordAsync(user, password))
            return Error.Validation("Gdpr.InvalidPassword", "Password is incorrect.");

        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state?.IsDataMasked == true) return Error.Conflict("Gdpr.AlreadyDeleted", "User data has already been erased.");
        if (state?.IsDeletionPending == true) return Error.Conflict("Gdpr.AlreadyRequested", "A deletion request is already pending.");

        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;
        var requestedAt = DateTimeOffset.UtcNow;
        var deadline = requestedAt.AddDays(deletionSettings.GraceDays);

        // Self-service grace model: the request schedules an auto-erase at the
        // grace deadline. The user STAYS active so they can log in and cancel
        // any time before the deadline (handled by CancelDeletionAsync + the
        // login interstitial). No confirm token — inaction now means deletion
        // proceeds, the inverse of the old confirm-within-7-days flow.
        state ??= new UserDeletionState { Id = userId };
        state.IsDeletionPending = true;
        state.DeletionInitiator = DeletionInitiator.SelfService;
        state.DeletionRequestedByUserId = null;
        state.DeletionRequestedAt = requestedAt;
        state.DeletionConfirmationDeadline = deadline;
        state.ReminderSentAt = null;
        state.DeletionReason = reason;
        session.Store(state);
        await session.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            var body = $"""
                <p>Your Modgud account is scheduled for permanent deletion on {deadline:f} (UTC).</p>
                <p>If you change your mind, log in any time before then and cancel the
                deletion — your account will be kept and nothing is lost.</p>
                <p>If you take no action, the account and its data are permanently erased
                on that date and cannot be recovered.</p>
                """;
            await emailService.SendEmailAsync(user.Email, "Your account is scheduled for deletion", body, ct);
        }

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Delete);

        return new DeletionRequestResponseDto
        {
            RequestedAt = requestedAt,
            ConfirmationDeadline = deadline,
            Message = $"Your account is scheduled for deletion on {deadline:D}. Log in before then to cancel.",
        };
    }

    public async Task<ErrorOr<bool>> CancelDeletionAsync(Guid userId, Guid? cancelledByAdminUserId = null, CancellationToken ct = default)
    {
        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state is null || !state.IsDeletionPending)
            return Error.Validation("Gdpr.NoPending", "No deletion request is pending.");

        // Admin cancel is the support escape hatch — it works on ANY pending
        // deletion regardless of initiator, and reactivates a user that was
        // deactivated into the admin recycle bin. A self-service cancel (no
        // admin id) only ever reaches self-pending users, who stay active.
        var wasAdminBin = state.DeletionInitiator == DeletionInitiator.Admin;

        state.IsDeletionPending = false;
        state.DeletionInitiator = null;
        state.DeletionRequestedByUserId = null;
        state.DeletionRequestedAt = null;
        state.DeletionConfirmationDeadline = null;
        state.ReminderSentAt = null;
        state.DeletionReason = null;
        session.Store(state);

        if (cancelledByAdminUserId is not null && wasAdminBin)
        {
            var appUser = await session.LoadAsync<ApplicationUser>(userId, ct);
            if (appUser is not null && !appUser.IsActive)
            {
                appUser.IsActive = true;
                session.Store(appUser);
                session.Events.Append(userId, new UserActivatedEvent(userId));
            }
        }

        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ErrorOr<DeletionStatusDto>> GetDeletionStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);

        return new DeletionStatusDto
        {
            IsPending = state?.IsDeletionPending ?? false,
            IsDeleted = user?.IsDeleted ?? false,
            IsDataMasked = state?.IsDataMasked ?? false,
            Initiator = state?.IsDeletionPending == true ? state.DeletionInitiator : null,
            RequestedAt = state?.DeletionRequestedAt,
            ConfirmationDeadline = state?.DeletionConfirmationDeadline,
        };
    }

    public async Task<ErrorOr<bool>> PermanentlyEraseAsync(Guid userId, Guid? adminUserId, string reason, CancellationToken ct = default)
    {
        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state?.IsDataMasked == true) return Error.Conflict("Gdpr.AlreadyMasked", "User data has already been erased.");

        return await PerformPermanentEraseAsync(userId, adminUserId, reason, state, ct);
    }

    private async Task<ErrorOr<bool>> PerformPermanentEraseAsync(Guid userId, Guid? adminUserId, string reason, UserDeletionState? existingState, CancellationToken ct)
    {
        // Prefer the AsyncLocal tenant (set by RealmMiddleware on requests AND by
        // the deletion sweep jobs via TenantContext.Enter) so a background erase
        // masks/archives in the correct realm DB — HttpContext is null there.
        var tenantId = TenantContext.CurrentOrNull
                       ?? httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
                       ?? throw new InvalidOperationException(
                           "Permanent erase requires an explicit realm context.");

        // 0) Revoke live access (OAuth grants + sessions + security stamp) BEFORE
        //    the user document is masked/deleted: the stamp rotation must load
        //    the not-yet-deleted user, and the stamp store (UserSecurityData) is
        //    dropped below. Reason=Deletion so consent grants are revoked too.
        await accessRevoker.RevokeAllAccessAsync(userId, AccessRevocationReason.Deletion, ct);

        // 1) Mark the user document as deleted + clear PII so any document-only
        //    consumers (Identity user store) immediately stop returning the user.
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is not null)
        {
            // ADR 0006 — an erased address must not leave a pending registration
            // behind either (hard delete; no-op when there is none).
            if (!string.IsNullOrEmpty(user.NormalizedEmail))
                session.Delete<Modgud.Authentication.Registration.PendingRegistration>(
                    Modgud.Authentication.Registration.PendingRegistration.IdFor(user.NormalizedEmail));

            user.IsDeleted = true;
            user.IsActive = false;
            user.UserName = $"deleted-{userId:N}";
            user.NormalizedUserName = user.UserName.ToUpperInvariant();
            user.Email = null;
            user.NormalizedEmail = null;
            user.Firstname = null;
            user.Lastname = null;
            user.Acronym = null;
            user.PasswordHash = null;
            user.AuthenticatorKey = null;
            user.TwoFactorEnabled = false;
            user.EmailOtpEnabled = false;
            session.Store(user);
        }

        // 1b) Scrub the PII-bearing projection read-models (audit remediation #20/#21).
        //     Marten masking rewrites event JSON only — it does NOT re-run projections —
        //     and the UserDeletedEvent Apply handlers merely flag IsDeleted while KEEPING
        //     Email/name. So without this the "forgotten" user's name + email survive in
        //     the queryable Principal/Person doc (also served by /api/principal/lookup)
        //     and the async UserView read-model (Art. 17 violation), and the Person stays
        //     IsActive=true (selectable in pickers / auto-group recompute). Hard-delete
        //     both docs; the stream is masked + archived below, so a rebuild (which
        //     excludes archived events) will not recreate them.
        session.Delete<Principal>(userId);
        session.Delete<UserView>(userId);

        // 2) Drop secondary documents (sessions + security data + change requests).
        session.DeleteWhere<UserSession>(s => s.UserId == userId);
        session.DeleteWhere<ClientSession>(s => s.UserId == userId);
        session.Delete<UserSecurityData>(userId);

        //    Federation v1: the per-user external-claims snapshot is a plain
        //    (non-event-sourced) doc keyed on the user id — a straight Delete
        //    fully erases its PII (no stream to mask). Rides this same batch.
        session.Delete<ExternalClaimsStore>(userId);

        //    WebAuthn passkeys are raw-crypto docs keyed on the user id — drop
        //    them so a permanently-erased user leaves no orphaned credentials.
        //    (Recycle-bin / deactivate must NOT do this — that path is reversible.)
        session.DeleteWhere<StoredPasskeyCredential>(c => c.UserId == userId);

        //    Terminal profile change-requests are retained for audit, but their
        //    payload carries the user's name/email — drop them so no plaintext
        //    PII survives the erase (the section comment above promised this).
        session.DeleteWhere<UserChangeRequest>(r => r.UserId == userId);

        //    Email-OTP challenge is a 1:1 doc (Id = userId) holding a plaintext
        //    email — drop it too.
        session.Delete<EmailOtpChallenge>(userId);

        //    External identity links carry Email, DisplayName, and the raw IdP
        //    claim payload on their OWN streams (keyed by link id). Drop the
        //    projection doc here; the PII-bearing events are masked + archived
        //    below alongside the user stream (archiving alone only flags the
        //    rows — the raw events must be masked to truly erase the PII).
        //    Discover the link ids two ways and union them:
        //      • live links via the projection-doc query, and
        //      • EVERY link the user ever held via the user stream's
        //        UserExternalIdentityLinkedEvent mirrors (each carries the
        //        LinkId). When a link is unlinked/re-homed (Variant C) the
        //        projection doc is dropped via ShouldDelete — but the stream is
        //        deliberately LEFT LIVE (un-archived), because Marten's masking
        //        does NOT rewrite already-archived streams. So the doc query
        //        alone misses the forgotten link, yet its still-live stream holds
        //        the PII; the user-stream mirror is the durable index back to it,
        //        and the mask + archive loops below scrub it during the erase.
        var liveLinkIds = (await session.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == userId)
                .ToListAsync(ct))
            .Select(l => l.Id);
        var historicalLinkIds = (await session.Events.FetchStreamAsync(userId, token: ct))
            .Select(e => e.Data)
            .OfType<UserExternalIdentityLinkedEvent>()
            .Select(e => e.LinkId);
        var linkIds = liveLinkIds.Concat(historicalLinkIds).Distinct().ToList();
        foreach (var linkId in linkIds)
            session.Delete<ExternalIdentityLink>(linkId);

        // 3) Update the deletion-state bookkeeping.
        var now = DateTimeOffset.UtcNow;
        var state = existingState ?? new UserDeletionState { Id = userId };
        state.IsDeletionPending = false;
        state.DeletionInitiator = null;
        state.DeletionRequestedByUserId = null;
        state.DeletionConfirmationDeadline = null;
        state.ReminderSentAt = null;
        state.IsDataMasked = true;
        state.DataMaskedAt = now;
        state.DataMaskedReason = reason;
        state.DataMaskedByUserId = adminUserId;
        session.Store(state);

        await session.SaveChangesAsync(ct);

        // 4) Apply Marten's PII masking to the user stream AND every external
        //    identity-link stream — replaces name/email/IP/raw-claim fields
        //    in-place using the rules registered on the StoreOptions. Masking
        //    must precede ArchiveStream: archiving only flags rows, the raw
        //    event data has to be rewritten for the PII to actually be gone.
        await store.Advanced.ApplyEventDataMasking(x =>
        {
            x.ForTenant(tenantId);
            x.IncludeStream(userId);
            foreach (var linkId in linkIds)
                x.IncludeStream(linkId);
            x.AddHeader("gdpr_masked", true);
            x.AddHeader("masked_at", now.ToString("O"));
            x.AddHeader("masked_by", adminUserId?.ToString() ?? "user_request");
            x.AddHeader("masking_reason", reason);
        }, ct);

        // ArchiveStream lives on a fresh session — IDocumentSession state is
        // already in the post-SaveChanges quiescent state above.
        await using var archiveSession = store.LightweightSession(tenantId);
        archiveSession.Events.ArchiveStream(userId);
        foreach (var linkId in linkIds)
            archiveSession.Events.ArchiveStream(linkId);
        await archiveSession.SaveChangesAsync(ct);

        // 5) Audit trail — mask-and-keep, NOT delete. The tenant audit is retained
        //    de-identified (Art-17(3)): the source events are now masked + archived,
        //    and AuthAuditViewProjection.IncludeArchivedEvents makes a rebuild
        //    regenerate these rows from the masked events. But masking appends no new
        //    event, so the LIVE (already-projected) rows still hold the pre-mask IP —
        //    null it here so the live view is immediately de-identified and identical
        //    to what an archived-inclusive rebuild produces. (Ip is the only PII
        //    column today; UserName is null, UserId is a pseudonymous tombstone key.)
        await using (var auditSession = store.LightweightSession(tenantId))
        {
            var auditRows = await auditSession.Query<Modgud.Authentication.Audit.AuthAuditView>()
                .Where(r => r.UserId == userId && r.Ip != null)
                .ToListAsync(ct);
            foreach (var row in auditRows)
                auditSession.Store(row with { Ip = null });
            await auditSession.SaveChangesAsync(ct);
        }

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Mask);

        return true;
    }

    /// <summary>
    /// Per-realm self-service sweep, run by the scheduled job inside the realm's
    /// tenant context: sends the "about to be deleted" reminder once per request
    /// (ReminderLeadDays before the deadline) and permanently erases self-service
    /// requests whose grace deadline has passed. Operates on the injected
    /// (tenant-scoped) session — the caller enters the tenant before resolving.
    /// </summary>
    public async Task<(int Reminded, int Erased)> RunSelfServiceSweepAsync(CancellationToken ct = default)
    {
        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;
        var now = DateTimeOffset.UtcNow;
        var reminderWindow = TimeSpan.FromDays(deletionSettings.ReminderLeadDays);

        // Small set (only users mid-grace); filter the initiator in memory to
        // avoid LINQ translation of the nullable enum comparison.
        var pending = (await session.Query<UserDeletionState>()
                .Where(s => s.IsDeletionPending)
                .ToListAsync(ct))
            .Where(s => s.DeletionInitiator == DeletionInitiator.SelfService
                        && s.DeletionConfirmationDeadline is not null)
            .ToList();

        var reminded = 0;
        var erased = 0;
        foreach (var state in pending)
        {
            var deadline = state.DeletionConfirmationDeadline!.Value;
            if (deadline <= now)
            {
                var result = await PerformPermanentEraseAsync(
                    state.Id, adminUserId: null, "Self-service grace period expired", state, ct);
                if (!result.IsError) erased++;
            }
            else if (state.ReminderSentAt is null && deadline - now <= reminderWindow)
            {
                await SendDeletionReminderAsync(state.Id, deadline, ct);
                state.ReminderSentAt = now;
                session.Store(state);
                await session.SaveChangesAsync(ct);
                reminded++;
            }
        }

        return (reminded, erased);
    }

    /// <summary>
    /// Per-realm admin recycle-bin auto-purge, run by the scheduled job inside
    /// the realm's tenant context: permanently erases admin-initiated pending
    /// deletions whose retention deadline has passed — but only when the realm
    /// has <see cref="DeletionSettings.AutoPurgeEnabled"/>. Manual ForceDelete
    /// (the permanent-erase endpoint) is unaffected by that toggle.
    /// </summary>
    public async Task<int> RunAdminRetentionPurgeAsync(CancellationToken ct = default)
    {
        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;
        if (!deletionSettings.AutoPurgeEnabled) return 0;

        var now = DateTimeOffset.UtcNow;
        var due = (await session.Query<UserDeletionState>()
                .Where(s => s.IsDeletionPending)
                .ToListAsync(ct))
            .Where(s => s.DeletionInitiator == DeletionInitiator.Admin
                        && s.DeletionConfirmationDeadline is not null
                        && s.DeletionConfirmationDeadline.Value <= now)
            .ToList();

        var purged = 0;
        foreach (var state in due)
        {
            var result = await PerformPermanentEraseAsync(
                state.Id, state.DeletionRequestedByUserId, "Admin recycle-bin retention expired", state, ct);
            if (!result.IsError) purged++;
        }

        return purged;
    }

    private async Task SendDeletionReminderAsync(Guid userId, DateTimeOffset deadline, CancellationToken ct)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (string.IsNullOrWhiteSpace(user?.Email)) return;

        var body = $"""
            <p>Reminder: your Modgud account is scheduled for permanent deletion on {deadline:f} (UTC).</p>
            <p>Log in before then and cancel the deletion if you want to keep your account.
            After that date the account and its data are erased and cannot be recovered.</p>
            """;
        await emailService.SendEmailAsync(user.Email, "Reminder: your account is about to be deleted", body, ct);
    }

    /// <summary>
    /// Pulls the most recent login events out of the user stream. Errors are
    /// swallowed (a missing/empty stream just yields an empty list) — export
    /// must never fail because the audit trail is unavailable.
    /// </summary>
    private async Task<List<ExportLoginEventDto>> GetLoginHistoryAsync(Guid userId, int maxEvents, CancellationToken ct)
    {
        var result = new List<ExportLoginEventDto>();
        try
        {
            var events = await session.Events.FetchStreamAsync(userId, token: ct);
            foreach (var e in events.OrderByDescending(x => x.Timestamp).Take(maxEvents * 2))
            {
                if (e.Data is UserLoggedInEvent loggedIn)
                {
                    result.Add(new ExportLoginEventDto
                    {
                        Timestamp = e.Timestamp,
                        Success = true,
                        IpAddress = loggedIn.IpAddress,
                    });
                }
                else if (e.Data is UserLoginFailedEvent failed)
                {
                    result.Add(new ExportLoginEventDto
                    {
                        Timestamp = e.Timestamp,
                        Success = false,
                        IpAddress = failed.IpAddress,
                    });
                }

                if (result.Count >= maxEvents) break;
            }
        }
        catch
        {
            // empty stream / archived stream — return what we have
        }
        return result;
    }
}
