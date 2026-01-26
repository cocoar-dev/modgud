namespace Cocoar.Auth.Web.Models;

// ═══════════════════════════════════════════════════════════════════════════
// AUTH MODELS
// ═══════════════════════════════════════════════════════════════════════════

public record LoginRequest(string UserName, string Password, bool RememberMe = false);

public record LoginResponse(bool Succeeded, bool RequiresTwoFactor = false, string? Error = null);

public record RegisterRequest(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string Password);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record ConfirmEmailRequest(string UserId, string Token);

public record UpdateProfileRequest(string FirstName, string LastName, string Email);

// ═══════════════════════════════════════════════════════════════════════════
// USER MODELS
// ═══════════════════════════════════════════════════════════════════════════

public class UserDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public bool IsLockedOut => LockoutEnd.HasValue && LockoutEnd > DateTimeOffset.UtcNow;
    public IEnumerable<string> Roles { get; set; } = [];
    public IEnumerable<ClaimDto> Claims { get; set; } = [];

    // GDPR Status
    public bool IsDeleted { get; set; }
    public bool IsDataMasked { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class UserProfileDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public IEnumerable<string> Roles { get; set; } = [];
}

public class ClaimDto
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public record CreateUserRequest(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string Password,
    List<string> Roles);

public record UpdateUserRequest(
    string Email,
    string FirstName,
    string LastName,
    bool EmailConfirmed,
    List<string> Roles);

public record AddClaimRequest(string Type, string Value);

// ═══════════════════════════════════════════════════════════════════════════
// ROLE MODELS
// ═══════════════════════════════════════════════════════════════════════════

public class RoleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public IEnumerable<ClaimDto> Claims { get; set; } = [];
}

public record CreateRoleRequest(string Name, string? Description = null);

public record UpdateRoleRequest(string? Description = null);

// ═══════════════════════════════════════════════════════════════════════════
// COMMON MODELS
// ═══════════════════════════════════════════════════════════════════════════

public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class ApiError
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Status { get; set; }
    public Dictionary<string, string[]>? Errors { get; set; }
}

public class CurrentUserInfo
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool IsAuthenticated { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
// TWO-FACTOR AUTHENTICATION MODELS
// ═══════════════════════════════════════════════════════════════════════════

public record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

public record TwoFactorStatusResponse(bool IsEnabled, bool HasAuthenticator, int RecoveryCodesRemaining);

public record RecoveryCodesResponse(List<string> Codes);

public record EnableTwoFactorRequest(string Code);

public record DisableTwoFactorRequest(string Code);

public record TwoFactorLoginRequest(string Code, bool RememberMachine = false);

public record RecoveryCodeLoginRequest(string Code);

public record TwoFactorLoginResponse(bool Succeeded, bool RequiresTwoFactor = false, string? Error = null);

// ═══════════════════════════════════════════════════════════════════════════
// SESSION MODELS
// ═══════════════════════════════════════════════════════════════════════════

public class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? Browser { get; set; }
    public string? BrowserVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? DeviceType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public bool IsCurrent { get; set; }
}

public record SessionListResponse(List<SessionDto> Sessions);

// ═══════════════════════════════════════════════════════════════════════════
// GDPR / DATA PROTECTION MODELS
// ═══════════════════════════════════════════════════════════════════════════

public record RequestDeletionRequest(string Password, string? Reason = null);

public record DeletionRequestResponse(DateTimeOffset RequestedAt, DateTimeOffset ConfirmationDeadline, string Message);

public record DeletionStatusResponse(
    bool IsPending,
    bool IsDeleted,
    bool IsDataMasked,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? ConfirmationDeadline);

public class UserDataExportResponse
{
    public ProfileData Profile { get; set; } = new();
    public SecurityData Security { get; set; } = new();
    public List<SessionExportData> Sessions { get; set; } = [];
    public List<LoginHistoryData> LoginHistory { get; set; } = [];
    public DateTimeOffset ExportedAt { get; set; }

    public class ProfileData
    {
        public string Id { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? PhoneNumber { get; set; }
        public bool EmailConfirmed { get; set; }
        public bool PhoneNumberConfirmed { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<string> Roles { get; set; } = [];
    }

    public class SecurityData
    {
        public bool TwoFactorEnabled { get; set; }
        public int RecoveryCodesRemaining { get; set; }
        public bool LockoutEnabled { get; set; }
        public DateTimeOffset? LockoutEnd { get; set; }
        public int AccessFailedCount { get; set; }
    }

    public class SessionExportData
    {
        public string Id { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public string? DeviceType { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActiveAt { get; set; }
    }

    public class LoginHistoryData
    {
        public DateTimeOffset Timestamp { get; set; }
        public string? IpAddress { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public bool Succeeded { get; set; }
        public string? FailureReason { get; set; }
    }
}

// Admin GDPR Models
public record SoftDeleteUserRequest(string? Reason = null);

public record RestoreUserRequest(string? Reason = null);

public record PermanentEraseUserRequest(string Reason);
