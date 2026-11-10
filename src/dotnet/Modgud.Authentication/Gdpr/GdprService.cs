using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Email;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Npgsql;

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
    IHttpContextAccessor httpContextAccessor,
    Modgud.Authentication.Devices.IDeviceTrust deviceTrust,
    ISecurityAuditLog audit) : IGdprService
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

        var history = await GetLoginHistoryAsync(userId, LoginHistoryLimit, ct);
        var consents = await GetConsentsAsync(userId, ct);

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Export);

        return new UserDataExportDto
        {
            Metadata = new ExportMetadataDto
            {
                ExportedAt = DateTimeOffset.UtcNow,
                FormatVersion = "1.2",
                UserId = userId,
                LoginHistoryLimit = LoginHistoryLimit,
                LoginHistoryTruncated = history.Truncated,
                LoginHistoryUnavailable = history.Unavailable,
                NotIncluded = ["permissions in applications other than the identity provider"],
            },
            OAuthConsents = consents,
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
            LoginHistory = history.Events,
        };
    }

    private const int LoginHistoryLimit = 100;

    /// <summary>A.gdpr-C005 — the OAuth consents the profile page promises: every valid
    /// authorization of the user with its application and scopes.</summary>
    private async Task<List<ExportConsentDto>> GetConsentsAsync(Guid userId, CancellationToken ct)
    {
        var subject = userId.ToString();
        var authorizations = await session.Query<Modgud.Domain.OAuth.Storage.OpenIddictAuthorizationDocument>()
            .Where(a => a.Subject == subject && a.Status == OpenIddict.Abstractions.OpenIddictConstants.Statuses.Valid)
            .ToListAsync(ct);
        var appIds = authorizations
            .Select(a => Guid.TryParse(a.ApplicationId, out var id) ? id : (Guid?)null)
            .OfType<Guid>().Distinct().ToList();
        var apps = appIds.Count == 0
            ? []
            : (await session.Query<Modgud.Domain.OAuth.Applications.OAuthApplicationState>()
                .Where(a => appIds.Contains(a.Id)).ToListAsync(ct)).ToDictionary(a => a.Id);
        return authorizations
            .OrderBy(a => a.CreationDate)
            .Select(a =>
            {
                var app = Guid.TryParse(a.ApplicationId, out var id) && apps.TryGetValue(id, out var found) ? found : null;
                return new ExportConsentDto
                {
                    ClientId = app?.ClientId,
                    ClientDisplayName = app?.DisplayName,
                    Scopes = [.. a.Scopes.Order(StringComparer.Ordinal)],
                    Type = a.Type,
                    GrantedAt = a.CreationDate,
                };
            })
            .ToList();
    }

    public async Task<ErrorOr<DeletionRequestResponseDto>> RequestDeletionAsync(Guid userId, string? password, string? reason, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("User.NotFound", $"User {userId} not found.");

        // ADR 0025 amendment C — the proof is the recent account factor (or, without one, a
        // recent sign-in), checked by the endpoint. A password was required here before,
        // which locked every user without one — e-mail-code users — out of deleting their
        // account. A password that is sent along is still checked.
        if (!string.IsNullOrEmpty(password) && !await userManager.CheckPasswordAsync(user, password))
            return Error.Validation("Gdpr.InvalidPassword", "Password is incorrect.");

        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state?.IsDataMasked == true) return Error.Conflict("Gdpr.AlreadyDeleted", "User data has already been erased.");
        if (state?.Erasure is not null) return Error.Conflict("Gdpr.ErasureInProgress", "Permanent erasure has already started and cannot be cancelled.");
        if (state?.IsDeletionPending == true) return Error.Conflict("Gdpr.AlreadyRequested", "A deletion request is already pending.");

        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;
        var requestedAt = DateTimeOffset.UtcNow;
        var deadline = requestedAt.AddDays(deletionSettings.GraceDays);

        // Self-service grace model: the request schedules an auto-erase at the
        // grace deadline. The user STAYS active so they can log in and cancel
        // any time before the deadline (handled by CancelDeletionAsync + the
        // login interstitial). No confirm token — inaction now means deletion
        // proceeds, the inverse of the old confirm-within-7-days flow.
        var isNewState = state is null;
        state ??= new UserDeletionState { Id = userId };
        state.IsDeletionPending = true;
        state.DeletionInitiator = DeletionInitiator.SelfService;
        state.DeletionRequestedByUserId = null;
        state.DeletionRequestedAt = requestedAt;
        state.DeletionConfirmationDeadline = deadline;
        state.ReminderSentAt = null;
        state.DeletionReason = reason;
        if (isNewState) session.Insert(state);
        else session.Store(state);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException or JasperFx.DocumentAlreadyExistsException)
        {
            return Error.Conflict("Gdpr.DeletionChanged", "The deletion state changed. Reload before retrying.");
        }

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
        if (state?.Erasure is not null)
            return Error.Conflict("Gdpr.ErasureInProgress", "Permanent erasure has already started and cannot be cancelled.");
        if (state is null || !state.IsDeletionPending)
            return Error.Validation("Gdpr.NoPending", "No deletion request is pending.");

        // Admin cancel is the support escape hatch — it works on ANY pending
        // deletion regardless of initiator, and reactivates a user that was
        // deactivated into the admin recycle bin. A self-service cancel (no
        // admin id) only ever reaches self-pending users, who stay active.
        var wasAdminBin = state.DeletionInitiator == DeletionInitiator.Admin || state.DeactivatedByAdminDelete;

        state.IsDeletionPending = false;
        state.DeactivatedByAdminDelete = false;
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

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            return Error.Conflict("Gdpr.DeletionChanged", "The deletion state changed. Reload before retrying.");
        }
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

    private const string AdminRetentionPurgeReason = "Admin recycle-bin retention expired";
    private const string SelfServiceGraceReason = "Self-service grace period expired";

    public async Task<ErrorOr<bool>> PermanentlyEraseAsync(Guid userId, Guid? adminUserId, string reason, CancellationToken ct = default)
    {
        await using var erasureLock = await TryAcquireErasureLockAsync(ct);
        if (erasureLock is null)
            return Error.Conflict("Gdpr.ErasureBusy", "Another erasure is running in this realm. Retry in a fresh scope.");
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

        // Persist the work's identity BEFORE revocation/secondary-doc commits.
        // An archived user stream no longer supplies historical link ids on a
        // retry, and the link projection documents are deleted below.
        var state = existingState ?? new UserDeletionState { Id = userId };
        if (state.Erasure is null)
        {
            var liveLinkIds = (await session.Query<ExternalIdentityLink>()
                    .Where(l => l.UserId == userId).ToListAsync(ct)).Select(l => l.Id);
            var historicalLinkIds = (await session.Events.FetchStreamAsync(userId, token: ct))
                .Select(e => e.Data).OfType<UserExternalIdentityLinkedEvent>().Select(e => e.LinkId);
            state.Erasure = new UserErasureProgress
            {
                StartedAt = DateTimeOffset.UtcNow,
                Reason = reason,
                AdminUserId = adminUserId,
                ExternalLinkIds = liveLinkIds.Concat(historicalLinkIds).Distinct().ToList(),
            };
            if (existingState is null) session.Insert(state);
            else session.Store(state);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException or JasperFx.DocumentAlreadyExistsException)
            {
                return Error.Conflict("Gdpr.DeletionChanged", "The deletion state changed. Reload before retrying.");
            }
        }
        var progress = state.Erasure;
        var linkIds = progress.ExternalLinkIds;

        if (!progress.DocumentsCleared)
        {
            // 0) Revoke live access before the user is deleted: stamp rotation
            // needs the still-loadable user and its security data. Do not repeat
            // this after document cleanup: it appends to the user's stream,
            // which might already have been archived by an earlier attempt.
            await accessRevoker.RevokeAllAccessAsync(userId, AccessRevocationReason.Deletion, ct);

            // 1) Immediately scrub the Identity document and stop document-only
            // consumers from loading this user.
            var user = await session.LoadAsync<ApplicationUser>(userId, ct);

            // B3-EI-01 / B10-PA04 — copies of the name outside the user's own stream.
            // Read before the change requests are deleted and the username is scrubbed.
            progress.InboxItemIds = await FindInboxItemIdsAsync(userId, ct);
            foreach (var itemId in progress.InboxItemIds)
                session.Delete<Modgud.Infrastructure.Persistence.Marten.Projections.Inbox.InboxItemView>(itemId);
            // B3-EI-06 — the per-source pointers to this user's open items name the user too.
            session.DeleteWhere<Modgud.Infrastructure.Inbox.InboxSourceSlot>(s => s.RecipientUserId == userId);
            await ClearPublisherSnapshotsAsync(userId, user?.UserName, ct);

            if (user is not null)
            {
                // ADR 0018 — hard-delete any matching pending registration.
                if (!string.IsNullOrEmpty(user.NormalizedEmail))
                    session.Delete<Modgud.Authentication.Registration.PendingRegistration>(
                        Modgud.Authentication.Registration.PendingRegistration.IdFor(user.NormalizedEmail));

                // ADR 0020 — remove this user from trusted devices. This helper
                // may commit, so DocumentsCleared must only be set AFTER it.
                await deviceTrust.ForgetUserAsync(userId, ct);

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

            // Masking rewrites event JSON, not existing projections. Delete both
            // PII-bearing read models explicitly; archived streams are excluded
            // from their rebuilds.
            session.Delete<Principal>(userId);
            session.Delete<UserView>(userId);
            // gscd — the groups would otherwise keep listing an id that resolves to nothing.
            await GroupMemberRemoval.RemoveFromAllGroupsAsync(session, userId, ct);

            // 2) Secondary documents. Keep the same erase set as before: no
            // changes to reversible recycle-bin/disable behavior.
            session.DeleteWhere<UserSession>(s => s.UserId == userId);
            session.DeleteWhere<ClientSession>(s => s.UserId == userId);
            session.Delete<UserSecurityData>(userId);
            session.Delete<ExternalClaimsStore>(userId);
            session.DeleteWhere<StoredPasskeyCredential>(c => c.UserId == userId);
            session.DeleteWhere<UserChangeRequest>(r => r.UserId == userId);
            session.DeleteWhere<EmailVerificationChallenge>(c => c.UserId == userId);
            session.Delete<EmailOtpChallenge>(userId);
            session.Delete<TestAccountEmailCode>(userId);
            session.Delete<Modgud.Domain.Dashboard.DashboardLayout>(
                Modgud.Domain.Dashboard.DashboardLayout.UserId(userId));
            foreach (var linkId in linkIds)
                session.Delete<ExternalIdentityLink>(linkId);

            // Commit this checkpoint in the SAME transaction as document cleanup,
            // never before a helper that might flush the injected session.
            progress.DocumentsCleared = true;
            session.Store(state);
            await session.SaveChangesAsync(ct);
        }

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
            foreach (var itemId in progress.InboxItemIds)
                x.IncludeStream(itemId);
            x.AddHeader("gdpr_masked", true);
            x.AddHeader("masked_at", progress.StartedAt.ToString("O"));
            x.AddHeader("masked_by", progress.AdminUserId?.ToString() ?? "user_request");
            x.AddHeader("masking_reason", progress.Reason);
        }, ct);

        // ArchiveStream lives on a fresh session — IDocumentSession state is
        // already in the post-SaveChanges quiescent state above.
        await using var archiveSession = store.LightweightSession(tenantId);
        archiveSession.Events.ArchiveStream(userId);
        foreach (var linkId in linkIds)
            archiveSession.Events.ArchiveStream(linkId);
        foreach (var itemId in progress.InboxItemIds)
            archiveSession.Events.ArchiveStream(itemId);
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

        // A.gdpr-C004 — the irreversible action gets its own required Security-log
        // event, committed with the tombstone below (who, whom, which path; never the
        // free-text reason). Read the path before the pending fields are cleared.
        // The path comes from the persisted intent, so a resumed erasure logs the same.
        var reasonCode = progress.Reason switch
        {
            AdminRetentionPurgeReason => "admin-retention-purge",
            SelfServiceGraceReason => "self-service-grace-expired",
            _ when progress.AdminUserId is not null => "admin-force-delete",
            _ => "system-erase",
        };
        var byAdmin = reasonCode == "admin-force-delete" && progress.AdminUserId is not null;
        audit.StoreRequired(session, new SecurityAuditRecord
        {
            EventType = AuditEvents.AccountErased,
            RealmSlug = tenantId,
            CaptureRequestContext = byAdmin,
            ActorKind = byAdmin ? AuditActorKind.User : AuditActorKind.System,
            ActorSubjectId = byAdmin ? progress.AdminUserId : null,
            TargetSubjectId = userId,
            OutcomeCode = AuditOutcomes.Succeeded,
            ReasonCode = reasonCode,
        });

        // Only every completed durable step makes this a terminal tombstone.
        // If this final commit fails, a fresh scope repeats the idempotent work
        // using the saved link inventory, even after all streams were archived.
        state.IsDeletionPending = false;
        state.DeletionInitiator = null;
        state.DeletionRequestedByUserId = null;
        state.DeletionConfirmationDeadline = null;
        state.ReminderSentAt = null;
        state.DeactivatedByAdminDelete = false;
        state.IsDataMasked = true;
        state.DataMaskedAt = DateTimeOffset.UtcNow;
        state.DataMaskedReason = progress.Reason;
        state.DataMaskedByUserId = progress.AdminUserId;
        state.Erasure = null;
        session.Store(state);
        await session.SaveChangesAsync(ct);

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Mask);

        return true;
    }

    /// <summary>
    /// B3-EI-01 — inbox item streams that carry this user's name: the admin items about
    /// their change requests (user label) and the items addressed to them (reviewer
    /// notes). Read from the events, not the async read model, so an item that is not
    /// projected yet is found too.
    /// </summary>
    private async Task<List<Guid>> FindInboxItemIdsAsync(Guid userId, CancellationToken ct)
    {
        var ids = (await session.Events.QueryRawEventDataOnly<Modgud.Application.Inbox.Events.InboxItemCreatedEvent>()
                .Where(e => e.RecipientUserId == userId)
                .ToListAsync(ct))
            .Select(e => e.Id)
            .ToList();
        var changeRequestIds = await session.Query<UserChangeRequest>()
            .Where(r => r.UserId == userId)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var changeRequestId in changeRequestIds)
        {
            var aboutRequest = await session.Events.QueryRawEventDataOnly<Modgud.Application.Inbox.Events.InboxItemCreatedEvent>()
                .Where(e => e.SourceType == Api.Account.ProfileEndpoints.ChangeRequestInboxSource && e.SourceId == changeRequestId)
                .ToListAsync(ct);
            ids.AddRange(aboutRequest.Select(e => e.Id));
        }
        return ids.Distinct().ToList();
    }

    /// <summary>
    /// B10-PA04 — the uploader of an asset and the publisher of page and composition
    /// revisions are kept as plain-text username snapshots. Clear the ones that name this
    /// user; the asset keeps its pseudonymous uploader id.
    /// </summary>
    private async Task ClearPublisherSnapshotsAsync(Guid userId, string? userName, CancellationToken ct)
    {
        var assets = await session.Query<Modgud.Domain.Assets.Asset>()
            .Where(a => a.UploadedByUserId == userId && a.UploadedByUsername != null)
            .ToListAsync(ct);
        foreach (var asset in assets)
        {
            asset.UploadedByUsername = null;
            session.Store(asset);
        }

        if (string.IsNullOrEmpty(userName)) return;
        var settings = await session.LoadAsync<Modgud.Domain.RealmSettings.RealmSettings>(
            Modgud.Domain.RealmSettings.RealmSettings.SingletonId, ct);
        if (settings is null) return;

        bool Names(string? publishedBy) =>
            publishedBy is not null && string.Equals(publishedBy, userName, StringComparison.OrdinalIgnoreCase);
        var changed = false;
        foreach (var version in settings.PageCompositions?.SelectMany(c => c.Versions) ?? [])
            if (Names(version.PublishedBy)) { version.PublishedBy = null; changed = true; }
        foreach (var revision in settings.PageSlots?.Values.SelectMany(s => s.Variants).SelectMany(v => v.Revisions) ?? [])
            if (Names(revision.PublishedBy)) { revision.PublishedBy = null; changed = true; }
        if (changed) session.Store(settings);
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
        await using var erasureLock = await TryAcquireErasureLockAsync(ct)
            ?? throw new InvalidOperationException("Another erasure is running in this realm. Retry the sweep in a fresh scope.");
        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;
        var now = DateTimeOffset.UtcNow;
        var reminderWindow = TimeSpan.FromDays(deletionSettings.ReminderLeadDays);

        // Keep the selected document/version bound to the intent commit below:
        // a cancel after this query must cause a conflict before erasure starts.
        var pending = (await session.Query<UserDeletionState>()
                .Where(s => s.IsDeletionPending || s.Erasure != null)
                .ToListAsync(ct))
            .Where(s => s.DeletionInitiator == DeletionInitiator.SelfService
                        && (s.Erasure is not null || s.DeletionConfirmationDeadline is not null))
            .ToList();

        var reminded = 0;
        var erased = 0;
        foreach (var state in pending)
        {
            var deadline = state.DeletionConfirmationDeadline;
            if (state.Erasure is not null || deadline <= now)
            {
                var result = await PerformPermanentEraseAsync(
                    state.Id, adminUserId: null, SelfServiceGraceReason, state, ct);
                // A failed optimistic commit may leave pending writes in this
                // session. Do not continue into another user's erasure with it.
                if (result.IsError)
                    throw new InvalidOperationException("Deletion state changed; retry the sweep in a fresh scope.");
                erased++;
            }
            else if (state.ReminderSentAt is null && deadline!.Value - now <= reminderWindow)
            {
                await SendDeletionReminderAsync(state.Id, deadline.Value, ct);
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
        await using var erasureLock = await TryAcquireErasureLockAsync(ct)
            ?? throw new InvalidOperationException("Another erasure is running in this realm. Retry the sweep in a fresh scope.");
        var deletionSettings = (await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults;

        var now = DateTimeOffset.UtcNow;
        var due = (await session.Query<UserDeletionState>()
                .Where(s => s.IsDeletionPending || s.Erasure != null)
                .ToListAsync(ct))
            // AutoPurge controls NEW erasures, not abandonment of an already
            // irreversible admin/manual erasure. Self-service resumes belong
            // to the other sweep so its telemetry is not counted as admin work.
            .Where(s => (s.Erasure is not null && s.DeletionInitiator != DeletionInitiator.SelfService)
                        || (deletionSettings.AutoPurgeEnabled
                            && s.DeletionInitiator == DeletionInitiator.Admin
                            && s.DeletionConfirmationDeadline is not null
                            && s.DeletionConfirmationDeadline.Value <= now))
            .ToList();

        var purged = 0;
        foreach (var state in due)
        {
            var result = await PerformPermanentEraseAsync(
                state.Id, state.DeletionRequestedByUserId, AdminRetentionPurgeReason, state, ct);
            if (result.IsError)
                throw new InvalidOperationException("Deletion state changed; retry the sweep in a fresh scope.");
            purged++;
        }

        return purged;
    }

    // Serializes the three erase entry points BEFORE they load candidates.
    // OCC alone cannot protect the revoker's separate Identity/event commits.
    // The dedicated connection targets the injected session's actual realm DB;
    // the same key therefore does not serialize unrelated realm databases.
    private const long ErasureLockKey = 0x4D47_4552_4153_4501; // "MGERASE" 1

    private async Task<IAsyncDisposable?> TryAcquireErasureLockAsync(CancellationToken ct)
    {
        var connection = session.Database.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", ErasureLockKey);
            if (await command.ExecuteScalarAsync(ct) is true)
                return new ErasureLock(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    private sealed class ErasureLock(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Request cancellation must not prevent explicit unlock on a
                // pooled connection. A lost lock connection is NOT fencing of
                // the other work connections; do not silently reconnect here.
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", ErasureLockKey);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
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
    /// <summary>The most recent <paramref name="maxEvents"/> login events of the user's
    /// stream. A.gdpr-C005 — scans the whole stream (older logins were dropped behind the
    /// last 200 events of any kind) and says when it truncated or could not read.</summary>
    private async Task<(List<ExportLoginEventDto> Events, bool Truncated, bool Unavailable)> GetLoginHistoryAsync(
        Guid userId, int maxEvents, CancellationToken ct)
    {
        IReadOnlyList<JasperFx.Events.IEvent> events;
        try
        {
            events = await session.Events.FetchStreamAsync(userId, token: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], false, true);
        }

        var logins = events
            .Select(e => e.Data switch
            {
                UserLoggedInEvent ok => new ExportLoginEventDto { Timestamp = e.Timestamp, Success = true, IpAddress = ok.IpAddress },
                UserLoginFailedEvent failed => new ExportLoginEventDto { Timestamp = e.Timestamp, Success = false, IpAddress = failed.IpAddress },
                _ => null,
            })
            .OfType<ExportLoginEventDto>()
            .OrderByDescending(e => e.Timestamp)
            .ToList();
        return ([.. logins.Take(maxEvents)], logins.Count > maxEvents, false);
    }
}
