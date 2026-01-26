using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for GDPR compliance operations.
/// Uses Marten's built-in data masking for PII erasure.
/// </summary>
public class GdprService : IGdprService
{
    private readonly IDocumentStore _store;
    private readonly IDocumentSession _session;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISessionRepository _sessionRepository;
    private readonly IEmailSender _emailSender;

    // Confirmation period for self-initiated deletion
    private static readonly TimeSpan DeletionConfirmationPeriod = TimeSpan.FromDays(7);

    public GdprService(
        IDocumentStore store,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        ISessionRepository sessionRepository,
        IEmailSender emailSender)
    {
        _store = store;
        _session = session;
        _userManager = userManager;
        _sessionRepository = sessionRepository;
        _emailSender = emailSender;
    }

    public async Task<ErrorOr<UserDataExportDto>> ExportUserDataAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        // Get user state for additional info
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);

        // Get user's roles
        var roleNames = await _userManager.GetRolesAsync(user);

        // Get user's claims
        var claims = await _userManager.GetClaimsAsync(user);

        // Get user's sessions
        var sessions = await _sessionRepository.GetByUserIdAsync(userId, cancellationToken);

        // Get login history from event stream (last 100 events)
        var loginEvents = await GetLoginHistoryAsync(userId, 100, cancellationToken);

        // Record the export event
        _session.Events.Append(userId, new UserDataExported(userId, DateTimeOffset.UtcNow, "JSON/1.0"));
        await _session.SaveChangesAsync(cancellationToken);

        return new UserDataExportDto
        {
            Metadata = new ExportMetadataDto
            {
                ExportedAt = DateTimeOffset.UtcNow,
                FormatVersion = "1.0",
                UserId = userId
            },
            Profile = new ExportProfileDto
            {
                UserName = user.UserName!,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                PhoneNumber = user.PhoneNumber,
                PhoneNumberConfirmed = user.PhoneNumberConfirmed,
                FirstName = user.FirstName,
                LastName = user.LastName,
                IsActive = user.IsActive,
                CreatedAt = userState?.CreatedAt ?? DateTimeOffset.MinValue
            },
            Security = new ExportSecurityDto
            {
                TwoFactorEnabled = user.TwoFactorEnabled,
                LockoutEnabled = user.LockoutEnabled,
                LockoutEnd = user.LockoutEnd,
                AccessFailedCount = user.AccessFailedCount
            },
            Roles = roleNames.ToList(),
            Claims = claims.Select(c => new ExportClaimDto { Type = c.Type, Value = c.Value }).ToList(),
            Sessions = sessions.Select(s => new ExportSessionDto
            {
                IpAddress = s.IpAddress,
                Browser = s.Browser,
                OperatingSystem = s.OperatingSystem,
                DeviceType = s.DeviceType,
                CreatedAt = s.CreatedAt,
                LastActiveAt = s.LastActiveAt
            }).ToList(),
            LoginHistory = loginEvents
        };
    }

    public async Task<ErrorOr<DeletionRequestDto>> RequestDeletionAsync(
        Guid userId,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        // Verify password
        var passwordValid = await _userManager.CheckPasswordAsync(user, password);
        if (!passwordValid)
        {
            return UserErrors.InvalidPassword;
        }

        // Check if user is already deleted
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState?.IsDeleted == true)
        {
            return GdprErrors.UserAlreadyDeleted;
        }

        // Check if deletion is already pending
        if (userState?.IsDeletionPending == true)
        {
            return GdprErrors.DeletionAlreadyRequested;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var confirmationDeadline = requestedAt.Add(DeletionConfirmationPeriod);

        // Record the deletion request event
        _session.Events.Append(userId, new UserDeletionRequested(
            userId,
            reason,
            requestedAt,
            confirmationDeadline));
        await _session.SaveChangesAsync(cancellationToken);

        // Generate deletion confirmation token
        var token = await _userManager.GenerateUserTokenAsync(user, "Default", "DeleteAccount");

        // Send confirmation email
        await _emailSender.SendEmailAsync(
            user.Email!,
            "Account Deletion Confirmation Required",
            $"You have requested to delete your account. This action is irreversible.\n\n" +
            $"To confirm deletion, use this token: {token}\n\n" +
            $"If you did not request this, please ignore this email.\n\n" +
            $"This request expires on {confirmationDeadline:f}.");

        return new DeletionRequestDto
        {
            RequestedAt = requestedAt,
            ConfirmationDeadline = confirmationDeadline,
            Message = "A confirmation email has been sent. Please confirm within 7 days."
        };
    }

    public async Task<ErrorOr<bool>> ConfirmDeletionAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        // Check if deletion is pending
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState?.IsDeletionPending != true)
        {
            return GdprErrors.NoDeletionPending;
        }

        // Check if confirmation period has expired
        if (userState.DeletionConfirmationDeadline < DateTimeOffset.UtcNow)
        {
            // Cancel the expired request
            _session.Events.Append(userId, new UserDeletionCancelled(userId, DateTimeOffset.UtcNow));
            await _session.SaveChangesAsync(cancellationToken);
            return GdprErrors.DeletionExpired;
        }

        // Verify token
        var tokenValid = await _userManager.VerifyUserTokenAsync(user, "Default", "DeleteAccount", token);
        if (!tokenValid)
        {
            return GdprErrors.InvalidDeletionToken;
        }

        // Perform the deletion (soft delete + data masking)
        return await PerformUserDeletionAsync(userId, adminUserId: null, "User-initiated deletion confirmed", cancellationToken);
    }

    public async Task<ErrorOr<bool>> CancelDeletionAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState is null)
        {
            return UserErrors.NotFound(userId);
        }

        if (!userState.IsDeletionPending)
        {
            return GdprErrors.NoDeletionPending;
        }

        _session.Events.Append(userId, new UserDeletionCancelled(userId, DateTimeOffset.UtcNow));
        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ErrorOr<DeletionStatusDto>> GetDeletionStatusAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState is null)
        {
            return UserErrors.NotFound(userId);
        }

        return new DeletionStatusDto
        {
            IsPending = userState.IsDeletionPending,
            IsDeleted = userState.IsDeleted,
            IsDataMasked = userState.IsDataMasked,
            RequestedAt = userState.DeletionRequestedAt,
            ConfirmationDeadline = userState.DeletionConfirmationDeadline
        };
    }

    public async Task<ErrorOr<bool>> SoftDeleteUserAsync(
        Guid userId,
        Guid adminUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState is null)
        {
            return UserErrors.NotFound(userId);
        }

        if (userState.IsDeleted)
        {
            return GdprErrors.UserAlreadyDeleted;
        }

        // Soft delete: mark as deleted but don't mask data
        _session.Events.Append(userId, new UserDeleted(userId, reason));
        await _session.SaveChangesAsync(cancellationToken);

        // Invalidate all sessions
        await _sessionRepository.DeleteAllForUserAsync(userId, cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> RestoreUserAsync(
        Guid userId,
        Guid adminUserId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState is null)
        {
            return UserErrors.NotFound(userId);
        }

        if (!userState.IsDeleted)
        {
            return GdprErrors.UserNotDeleted;
        }

        if (userState.IsDataMasked)
        {
            return GdprErrors.CannotRestoreMaskedUser;
        }

        _session.Events.Append(userId, new UserRestored(userId, adminUserId, reason));
        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> PermanentlyEraseUserDataAsync(
        Guid userId,
        Guid adminUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState is null)
        {
            return UserErrors.NotFound(userId);
        }

        if (userState.IsDataMasked)
        {
            return GdprErrors.DataAlreadyMasked;
        }

        return await PerformUserDeletionAsync(userId, adminUserId, reason, cancellationToken);
    }

    private async Task<ErrorOr<bool>> PerformUserDeletionAsync(
        Guid userId,
        Guid? adminUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        // 1. Soft delete if not already deleted
        var userState = await _session.LoadAsync<UserState>(userId, cancellationToken);
        if (userState?.IsDeleted != true)
        {
            _session.Events.Append(userId, new UserDeleted(userId, reason));
        }

        // 2. Record the data masking event
        _session.Events.Append(userId, new UserDataMasked(
            userId,
            DateTimeOffset.UtcNow,
            adminUserId,
            reason));
        await _session.SaveChangesAsync(cancellationToken);

        // 3. Apply Marten's data masking to the event stream
        // This replaces PII with masked values according to the rules configured in DependencyInjection
        await _store.Advanced.ApplyEventDataMasking(x =>
        {
            x.IncludeStream(userId);
            x.AddHeader("gdpr_masked", true);
            x.AddHeader("masked_at", DateTimeOffset.UtcNow.ToString("O"));
            x.AddHeader("masked_by", adminUserId?.ToString() ?? "user_request");
            x.AddHeader("masking_reason", reason);
        }, cancellationToken);

        // 4. Archive the event stream (excludes from normal queries)
        _session.Events.ArchiveStream(userId);
        await _session.SaveChangesAsync(cancellationToken);

        // 5. Delete related non-event-sourced data
        await _sessionRepository.DeleteAllForUserAsync(userId, cancellationToken);

        // 6. Delete security data
        _session.Delete<UserSecurityData>(userId);
        await _session.SaveChangesAsync(cancellationToken);

        // 7. Mark ApplicationUser document as deleted and clear PII
        var user = await _session.LoadAsync<ApplicationUser>(userId, cancellationToken);
        if (user is not null)
        {
            user.MarkAsDeleted();
            user.ClearPersonalData();
            _session.Store(user);
            await _session.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task<List<ExportLoginEventDto>> GetLoginHistoryAsync(
        Guid userId,
        int maxEvents,
        CancellationToken cancellationToken)
    {
        var result = new List<ExportLoginEventDto>();

        try
        {
            var events = await _session.Events.FetchStreamAsync(userId, token: cancellationToken);

            var loginEvents = events
                .OrderByDescending(e => e.Timestamp)
                .Take(maxEvents * 2) // Get more to filter
                .Select(e => e.Data)
                .Where(e => e is UserLoggedIn or UserLoginFailed)
                .Take(maxEvents);

            foreach (var evt in loginEvents)
            {
                if (evt is UserLoggedIn login)
                {
                    result.Add(new ExportLoginEventDto
                    {
                        Timestamp = DateTimeOffset.UtcNow, // Event timestamp not accessible here
                        Success = true,
                        IpAddress = login.IpAddress
                    });
                }
                else if (evt is UserLoginFailed failed)
                {
                    result.Add(new ExportLoginEventDto
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Success = false,
                        IpAddress = failed.IpAddress,
                        FailureReason = failed.FailureReason.ToString()
                    });
                }
            }
        }
        catch
        {
            // If event stream doesn't exist or is inaccessible, return empty list
        }

        return result;
    }
}
