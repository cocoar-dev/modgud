using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Services;
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
    IHttpContextAccessor httpContextAccessor) : IGdprService
{
    /// <summary>How long the user has to confirm a self-initiated
    /// deletion before the request is automatically cancelled.</summary>
    private static readonly TimeSpan DeletionConfirmationPeriod = TimeSpan.FromDays(7);

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

        var loginHistory = await GetLoginHistoryAsync(userId, 100, ct);

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Export);

        return new UserDataExportDto
        {
            Metadata = new ExportMetadataDto
            {
                ExportedAt = DateTimeOffset.UtcNow,
                FormatVersion = "1.0",
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
                IpAddress = s.IpAddress,
                Browser = s.Browser,
                OperatingSystem = s.OperatingSystem,
                DeviceType = s.DeviceType,
                CreatedAt = s.CreatedAt,
                LastActiveAt = s.LastActiveAt,
            }).ToList(),
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

        var requestedAt = DateTimeOffset.UtcNow;
        var deadline = requestedAt.Add(DeletionConfirmationPeriod);

        state ??= new UserDeletionState { Id = userId };
        state.IsDeletionPending = true;
        state.DeletionRequestedAt = requestedAt;
        state.DeletionConfirmationDeadline = deadline;
        state.DeletionReason = reason;
        session.Store(state);
        await session.SaveChangesAsync(ct);

        var token = await userManager.GenerateUserTokenAsync(user, "Default", "DeleteAccount");

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            var body = $"""
                <p>You have requested to permanently delete your Modgud account.</p>
                <p>To confirm, submit this token in the app within 7 days:</p>
                <pre>{token}</pre>
                <p>If you did not request this, you can ignore this email — your account
                stays untouched. The request will expire automatically on {deadline:f} (UTC).</p>
                """;
            await emailService.SendEmailAsync(user.Email, "Confirm account deletion", body, ct);
        }

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Delete);

        return new DeletionRequestResponseDto
        {
            RequestedAt = requestedAt,
            ConfirmationDeadline = deadline,
            Message = "A confirmation email has been sent. Please confirm within 7 days.",
        };
    }

    public async Task<ErrorOr<bool>> ConfirmDeletionAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Error.NotFound("User.NotFound", $"User {userId} not found.");

        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state is null || !state.IsDeletionPending)
            return Error.Validation("Gdpr.NoPending", "No deletion request is pending.");

        if (state.DeletionConfirmationDeadline is { } deadline && deadline < DateTimeOffset.UtcNow)
        {
            // Auto-cancel an expired pending request — same outcome the user would get
            // if they hit /cancel-deletion at this point.
            state.IsDeletionPending = false;
            session.Store(state);
            await session.SaveChangesAsync(ct);
            return Error.Validation("Gdpr.Expired", "Deletion request has expired.");
        }

        if (!await userManager.VerifyUserTokenAsync(user, "Default", "DeleteAccount", token))
            return Error.Validation("Gdpr.InvalidToken", "Confirmation token is invalid.");

        return await PerformPermanentEraseAsync(userId, adminUserId: null, "User-confirmed deletion", state, ct);
    }

    public async Task<ErrorOr<bool>> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var state = await session.LoadAsync<UserDeletionState>(userId, ct);
        if (state is null || !state.IsDeletionPending)
            return Error.Validation("Gdpr.NoPending", "No deletion request is pending.");

        state.IsDeletionPending = false;
        state.DeletionRequestedAt = null;
        state.DeletionConfirmationDeadline = null;
        state.DeletionReason = null;
        session.Store(state);
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
        var tenantId = httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string
                       ?? TenantConstants.SystemTenantId;

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

        // 2) Drop secondary documents (sessions + security data + change requests).
        session.DeleteWhere<UserSession>(s => s.UserId == userId);
        session.Delete<UserSecurityData>(userId);

        //    Federation v1: the per-user external-claims snapshot is a plain
        //    (non-event-sourced) doc keyed on the user id — a straight Delete
        //    fully erases its PII (no stream to mask). Rides this same batch.
        session.Delete<ExternalClaimsStore>(userId);

        //    External identity links carry Email, DisplayName, and the raw IdP
        //    claim payload on their OWN streams (keyed by link id). Drop the
        //    projection doc here; the PII-bearing events are masked + archived
        //    below alongside the user stream (archiving alone only flags the
        //    rows — the raw events must be masked to truly erase the PII).
        //    Include already-unlinked tombstones, which still hold that PII
        //    (so drop the !IsUnlinked filter).
        var linkIds = (await session.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == userId)
                .ToListAsync(ct))
            .Select(l => l.Id)
            .ToList();
        foreach (var linkId in linkIds)
            session.Delete<ExternalIdentityLink>(linkId);

        // 3) Update the deletion-state bookkeeping.
        var now = DateTimeOffset.UtcNow;
        var state = existingState ?? new UserDeletionState { Id = userId };
        state.IsDeletionPending = false;
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

        ModgudMeters.RecordGdprRequest(ModgudMeters.GdprRequestType.Mask);

        return true;
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
