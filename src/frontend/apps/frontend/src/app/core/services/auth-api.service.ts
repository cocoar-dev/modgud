import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  LoginRequest,
  LoginResult,
  CurrentUser,
  RegisterRequest,
  RegisterResult,
  ForgotPasswordRequest,
  ResetPasswordRequest,
  UpdateProfileRequest,
  Profile,
  TwoFactorSetup,
  EnableTwoFactorRequest,
  DisableTwoFactorRequest,
  TwoFactorStatus,
  RecoveryCodes,
  TwoFactorLoginRequest,
  RecoveryCodeLoginRequest,
  SessionList,
  RequestDeletionRequest,
  ConfirmDeletionRequest,
  DeletionRequestResult,
  DeletionStatus,
  UserDataExport,
  ChangePasswordRequest,
  ResendConfirmationRequest,
  SetupStatus,
  CreateAdminRequest,
  SetupResult,
} from '../models/auth.models';

@Injectable({
  providedIn: 'root',
})
export class AuthApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl;

  // ============================================================================
  // Public Authentication
  // ============================================================================

  login(request: LoginRequest): Observable<LoginResult> {
    return this.http.post<LoginResult>(`${this.baseUrl}/auth/login`, request);
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/logout`, {});
  }

  register(request: RegisterRequest): Observable<RegisterResult> {
    return this.http.post<RegisterResult>(
      `${this.baseUrl}/auth/register`,
      request
    );
  }

  confirmEmail(userId: string, token: string): Observable<void> {
    const params = new HttpParams().set('userId', userId).set('token', token);
    return this.http.get<void>(`${this.baseUrl}/auth/confirm-email`, {
      params,
    });
  }

  resendConfirmation(request: ResendConfirmationRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/auth/resend-confirmation`,
      request
    );
  }

  forgotPassword(request: ForgotPasswordRequest): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/forgot-password`, request);
  }

  resetPassword(request: ResetPasswordRequest): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/reset-password`, request);
  }

  getCurrentUser(): Observable<CurrentUser> {
    return this.http.get<CurrentUser>(`${this.baseUrl}/auth/me`);
  }

  // ============================================================================
  // User Profile
  // ============================================================================

  getProfile(): Observable<Profile> {
    return this.http.get<Profile>(`${this.baseUrl}/auth/profile`);
  }

  updateProfile(request: UpdateProfileRequest): Observable<Profile> {
    return this.http.put<Profile>(`${this.baseUrl}/auth/profile`, request);
  }

  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/auth/change-password`,
      request
    );
  }

  // ============================================================================
  // Two-Factor Authentication
  // ============================================================================

  getTwoFactorStatus(): Observable<TwoFactorStatus> {
    return this.http.get<TwoFactorStatus>(`${this.baseUrl}/auth/2fa/status`);
  }

  setupTwoFactor(): Observable<TwoFactorSetup> {
    return this.http.post<TwoFactorSetup>(`${this.baseUrl}/auth/2fa/setup`, {});
  }

  enableTwoFactor(request: EnableTwoFactorRequest): Observable<RecoveryCodes> {
    return this.http.post<RecoveryCodes>(
      `${this.baseUrl}/auth/2fa/enable`,
      request
    );
  }

  disableTwoFactor(request: DisableTwoFactorRequest): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/2fa/disable`, request);
  }

  generateRecoveryCodes(): Observable<RecoveryCodes> {
    return this.http.post<RecoveryCodes>(
      `${this.baseUrl}/auth/2fa/recovery-codes`,
      {}
    );
  }

  twoFactorLogin(request: TwoFactorLoginRequest): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/2fa/login`, request);
  }

  recoveryCodeLogin(request: RecoveryCodeLoginRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/auth/2fa/recovery-login`,
      request
    );
  }

  // ============================================================================
  // Session Management
  // ============================================================================

  getSessions(): Observable<SessionList> {
    return this.http.get<SessionList>(`${this.baseUrl}/auth/sessions`);
  }

  revokeSession(sessionId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/auth/sessions/${sessionId}`);
  }

  revokeAllSessions(): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/auth/sessions`);
  }

  // ============================================================================
  // GDPR / Data Protection
  // ============================================================================

  exportData(): Observable<UserDataExport> {
    return this.http.get<UserDataExport>(`${this.baseUrl}/auth/export-data`);
  }

  requestDeletion(
    request: RequestDeletionRequest
  ): Observable<DeletionRequestResult> {
    return this.http.post<DeletionRequestResult>(
      `${this.baseUrl}/auth/delete-account`,
      request
    );
  }

  confirmDeletion(request: ConfirmDeletionRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/auth/confirm-deletion`,
      request
    );
  }

  cancelDeletion(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/cancel-deletion`, {});
  }

  getDeletionStatus(): Observable<DeletionStatus> {
    return this.http.get<DeletionStatus>(`${this.baseUrl}/auth/deletion-status`);
  }

  // ============================================================================
  // Setup (First-time configuration)
  // ============================================================================

  getSetupStatus(): Observable<SetupStatus> {
    return this.http.get<SetupStatus>(`${this.baseUrl}/setup/status`);
  }

  createAdmin(request: CreateAdminRequest): Observable<SetupResult> {
    return this.http.post<SetupResult>(`${this.baseUrl}/setup/create-admin`, request);
  }
}
