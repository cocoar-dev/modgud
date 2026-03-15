import { http } from './http';
import type {
  LoginRequest,
  LoginResult,
  CurrentUser,
  RegisterRequest,
  RegisterResult,
  ForgotPasswordRequest,
  ResetPasswordRequest,
  UpdateProfileRequest,
  Profile,
  ChangePasswordRequest,
  TwoFactorSetup,
  EnableTwoFactorRequest,
  DisableTwoFactorRequest,
  TwoFactorStatus,
  RecoveryCodes,
  TwoFactorLoginRequest,
  RecoveryCodeLoginRequest,
  SessionList,
  DeletionStatus,
  RequestDeletionRequest,
  ConfirmDeletionRequest,
  SetupStatus,
  CreateAdminRequest,
  SetupResult,
} from '../models/auth.models';

export const authApi = {
  // Auth
  login: (req: LoginRequest) => http.post<LoginResult>('/auth/login', req),
  logout: () => http.post<void>('/auth/logout', {}),
  register: (req: RegisterRequest) => http.post<RegisterResult>('/auth/register', req),
  confirmEmail: (userId: string, token: string) =>
    http.get<void>(`/auth/confirm-email?userId=${encodeURIComponent(userId)}&token=${encodeURIComponent(token)}`),
  forgotPassword: (req: ForgotPasswordRequest) => http.post<void>('/auth/forgot-password', req),
  resetPassword: (req: ResetPasswordRequest) => http.post<void>('/auth/reset-password', req),
  getCurrentUser: () => http.get<CurrentUser>('/auth/me'),

  // Profile
  getProfile: () => http.get<Profile>('/auth/profile'),
  updateProfile: (req: UpdateProfileRequest) => http.put<Profile>('/auth/profile', req),
  changePassword: (req: ChangePasswordRequest) => http.post<void>('/auth/change-password', req),

  // 2FA
  getTwoFactorStatus: () => http.get<TwoFactorStatus>('/auth/2fa/status'),
  setupTwoFactor: () => http.post<TwoFactorSetup>('/auth/2fa/setup', {}),
  enableTwoFactor: (req: EnableTwoFactorRequest) => http.post<RecoveryCodes>('/auth/2fa/enable', req),
  disableTwoFactor: (req: DisableTwoFactorRequest) => http.post<void>('/auth/2fa/disable', req),
  generateRecoveryCodes: () => http.post<RecoveryCodes>('/auth/2fa/recovery-codes', {}),
  twoFactorLogin: (req: TwoFactorLoginRequest) => http.post<void>('/auth/2fa/login', req),
  recoveryCodeLogin: (req: RecoveryCodeLoginRequest) => http.post<void>('/auth/2fa/recovery-login', req),

  // Sessions
  getSessions: () => http.get<SessionList>('/auth/sessions'),
  revokeSession: (id: string) => http.delete<void>(`/auth/sessions/${id}`),
  revokeAllSessions: () => http.delete<void>('/auth/sessions'),

  // GDPR
  exportData: () => http.get<unknown>('/auth/export-data'),
  getDeletionStatus: () => http.get<DeletionStatus>('/auth/deletion-status'),
  requestDeletion: (req: RequestDeletionRequest) => http.post<void>('/auth/delete-account', req),
  confirmDeletion: (req: ConfirmDeletionRequest) => http.post<void>('/auth/confirm-deletion', req),
  cancelDeletion: () => http.post<void>('/auth/cancel-deletion', {}),

  // Setup
  getSetupStatus: () => http.get<SetupStatus>('/setup/status'),
  createAdmin: (req: CreateAdminRequest) => http.post<SetupResult>('/setup/create-admin', req),
};
