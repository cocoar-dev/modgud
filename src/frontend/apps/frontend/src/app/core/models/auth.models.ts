// ============================================================================
// Authentication DTOs
// ============================================================================

export interface LoginRequest {
  userName: string;
  password: string;
  rememberMe: boolean;
}

export interface LoginResult {
  succeeded: boolean;
  requiresTwoFactor: boolean;
  isLockedOut: boolean;
  isNotAllowed: boolean;
  errorMessage?: string;
}

export interface CurrentUser {
  id: string;
  userName: string;
  email?: string;
  firstName?: string;
  lastName?: string;
  roles: string[];
}

export interface RegisterRequest {
  userName: string;
  email: string;
  password: string;
  firstName?: string;
  lastName?: string;
}

export interface RegisterResult {
  succeeded: boolean;
  userId?: string;
  requiresEmailConfirmation: boolean;
  errors: string[];
}

export interface ConfirmEmailRequest {
  userId: string;
  token: string;
}

export interface ResendConfirmationRequest {
  email: string;
}

export interface ForgotPasswordRequest {
  email: string;
}

export interface ResetPasswordRequest {
  email: string;
  token: string;
  newPassword: string;
}

export interface UpdateProfileRequest {
  firstName?: string;
  lastName?: string;
  phoneNumber?: string;
}

export interface Profile {
  id: string;
  userName: string;
  email?: string;
  emailConfirmed: boolean;
  firstName?: string;
  lastName?: string;
  phoneNumber?: string;
  phoneNumberConfirmed: boolean;
  twoFactorEnabled: boolean;
  createdAt: string;
}

// ============================================================================
// Two-Factor Authentication DTOs
// ============================================================================

export interface TwoFactorSetup {
  sharedKey: string;
  authenticatorUri: string;
}

export interface EnableTwoFactorRequest {
  code: string;
}

export interface DisableTwoFactorRequest {
  code: string;
}

export interface TwoFactorStatus {
  isEnabled: boolean;
  hasAuthenticator: boolean;
  recoveryCodesRemaining: number;
}

export interface RecoveryCodes {
  codes: string[];
}

export interface TwoFactorLoginRequest {
  code: string;
  rememberMachine: boolean;
}

export interface RecoveryCodeLoginRequest {
  code: string;
}

// ============================================================================
// Session DTOs
// ============================================================================

export interface Session {
  id: string;
  ipAddress?: string;
  browser?: string;
  browserVersion?: string;
  operatingSystem?: string;
  osVersion?: string;
  deviceType?: string;
  createdAt: string;
  lastActiveAt: string;
  isCurrent: boolean;
}

export interface SessionList {
  sessions: Session[];
}

// ============================================================================
// GDPR / Data Protection DTOs
// ============================================================================

export interface RequestDeletionRequest {
  password: string;
  reason?: string;
}

export interface ConfirmDeletionRequest {
  token: string;
}

export interface DeletionRequestResult {
  requestedAt: string;
  confirmationDeadline: string;
  message: string;
}

export interface DeletionStatus {
  isPending: boolean;
  isDeleted: boolean;
  isDataMasked: boolean;
  requestedAt?: string;
  confirmationDeadline?: string;
}

export interface UserDataExport {
  metadata: ExportMetadata;
  profile: ExportProfile;
  security: ExportSecurity;
  roles: string[];
  claims: ExportClaim[];
  sessions: ExportSession[];
  loginHistory: ExportLoginEvent[];
}

export interface ExportMetadata {
  exportedAt: string;
  formatVersion: string;
  userId: string;
}

export interface ExportProfile {
  userName: string;
  email?: string;
  emailConfirmed: boolean;
  phoneNumber?: string;
  phoneNumberConfirmed: boolean;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  createdAt: string;
}

export interface ExportSecurity {
  twoFactorEnabled: boolean;
  lockoutEnabled: boolean;
  lockoutEnd?: string;
  accessFailedCount: number;
}

export interface ExportClaim {
  type: string;
  value: string;
}

export interface ExportSession {
  ipAddress?: string;
  browser?: string;
  operatingSystem?: string;
  deviceType?: string;
  createdAt: string;
  lastActiveAt: string;
}

export interface ExportLoginEvent {
  timestamp: string;
  success: boolean;
  ipAddress?: string;
  failureReason?: string;
}

// ============================================================================
// Admin User DTOs
// ============================================================================

export interface User {
  id: string;
  userName: string;
  email?: string;
  emailConfirmed: boolean;
  phoneNumber?: string;
  phoneNumberConfirmed: boolean;
  twoFactorEnabled: boolean;
  lockoutEnd?: string;
  lockoutEnabled: boolean;
  accessFailedCount: number;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  createdAt: string;
  modifiedAt?: string;
  roles: string[];
}

export interface CreateUserRequest {
  userName: string;
  password: string;
  email?: string;
  phoneNumber?: string;
  firstName?: string;
  lastName?: string;
  isActive?: boolean;
  lockoutEnabled?: boolean;
  roles?: string[];
}

export interface UpdateUserRequest {
  userName?: string;
  email?: string | null;
  phoneNumber?: string | null;
  firstName?: string | null;
  lastName?: string | null;
  isActive?: boolean;
  lockoutEnabled?: boolean;
  emailConfirmed?: boolean;
  phoneNumberConfirmed?: boolean;
  twoFactorEnabled?: boolean;
  roles?: string[];
}

export interface UserList {
  items: User[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AdminResetPasswordRequest {
  newPassword: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface AdminSoftDeleteRequest {
  reason?: string;
}

export interface AdminRestoreRequest {
  reason?: string;
}

export interface AdminPermanentEraseRequest {
  reason: string;
}

// ============================================================================
// Admin Role DTOs
// ============================================================================

export interface Role {
  id: string;
  name: string;
  description?: string;
  createdAt: string;
  modifiedAt?: string;
}

export interface CreateRoleRequest {
  name: string;
  description?: string;
}

export interface UpdateRoleRequest {
  name?: string;
  description?: string | null;
}

export interface RoleList {
  items: Role[];
  totalCount: number;
}

// ============================================================================
// Common DTOs
// ============================================================================

export interface ApiError {
  code: string;
  message: string;
  errors?: Record<string, string[]>;
}

export interface PaginationParams {
  page?: number;
  pageSize?: number;
  search?: string;
  sortBy?: string;
  sortDescending?: boolean;
}

// ============================================================================
// Setup DTOs
// ============================================================================

export interface SetupStatus {
  needsSetup: boolean;
}

export interface CreateAdminRequest {
  userName: string;
  password: string;
  email?: string;
  firstName?: string;
  lastName?: string;
}

export interface SetupResult {
  success: boolean;
  message: string;
}
