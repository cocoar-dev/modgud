import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  User,
  UserList,
  CreateUserRequest,
  UpdateUserRequest,
  AdminResetPasswordRequest,
  SessionList,
  AdminSoftDeleteRequest,
  AdminRestoreRequest,
  AdminPermanentEraseRequest,
  DeletionStatus,
  Role,
  RoleList,
  CreateRoleRequest,
  UpdateRoleRequest,
  PaginationParams,
  Realm,
  RealmList,
  CreateRealmRequest,
  UpdateRealmRequest,
} from '../models/auth.models';
import { RealmContextService } from './realm-context.service';
import {
  OAuthClient,
  OAuthClientList,
  CreateOAuthClientRequest,
  UpdateOAuthClientRequest,
  OAuthClientCreated,
  ClientSecret,
  OAuthScope,
  OAuthScopeList,
  CreateOAuthScopeRequest,
  UpdateOAuthScopeRequest,
  OAuthApi,
  OAuthApiList,
  CreateOAuthApiRequest,
  UpdateOAuthApiRequest,
  OAuthApiCreated,
  ApiSecret,
} from '../models/oauth.models';

@Injectable({
  providedIn: 'root',
})
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(RealmContextService).apiUrl;

  // ============================================================================
  // User Management
  // ============================================================================

  getUsers(params?: PaginationParams): Observable<UserList> {
    let httpParams = new HttpParams();
    if (params) {
      if (params.page !== undefined) {
        httpParams = httpParams.set('page', params.page.toString());
      }
      if (params.pageSize !== undefined) {
        httpParams = httpParams.set('pageSize', params.pageSize.toString());
      }
      if (params.search) {
        httpParams = httpParams.set('search', params.search);
      }
      if (params.sortBy) {
        httpParams = httpParams.set('sortBy', params.sortBy);
      }
      if (params.sortDescending !== undefined) {
        httpParams = httpParams.set(
          'sortDescending',
          params.sortDescending.toString()
        );
      }
    }
    return this.http.get<UserList>(`${this.baseUrl}/admin/users`, {
      params: httpParams,
    });
  }

  getUser(id: string): Observable<User> {
    return this.http.get<User>(`${this.baseUrl}/admin/users/${id}`);
  }

  createUser(request: CreateUserRequest): Observable<User> {
    return this.http.post<User>(`${this.baseUrl}/admin/users`, request);
  }

  updateUser(id: string, request: UpdateUserRequest): Observable<User> {
    return this.http.patch<User>(`${this.baseUrl}/admin/users/${id}`, request);
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/users/${id}`);
  }

  resetUserPassword(id: string, request: AdminResetPasswordRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/admin/users/${id}/reset-password`,
      request
    );
  }

  unlockUser(id: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/admin/users/${id}/unlock`, {});
  }

  getUserSessions(id: string): Observable<SessionList> {
    return this.http.get<SessionList>(
      `${this.baseUrl}/admin/users/${id}/sessions`
    );
  }

  revokeUserSessions(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/users/${id}/sessions`);
  }

  softDeleteUser(id: string, request?: AdminSoftDeleteRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/admin/users/${id}/soft-delete`,
      request || {}
    );
  }

  restoreUser(id: string, request?: AdminRestoreRequest): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/admin/users/${id}/restore`,
      request || {}
    );
  }

  permanentlyEraseUser(
    id: string,
    request: AdminPermanentEraseRequest
  ): Observable<void> {
    return this.http.delete<void>(
      `${this.baseUrl}/admin/users/${id}/permanent`,
      { body: request }
    );
  }

  getUserDeletionStatus(id: string): Observable<DeletionStatus> {
    return this.http.get<DeletionStatus>(
      `${this.baseUrl}/admin/users/${id}/deletion-status`
    );
  }

  // ============================================================================
  // Role Management
  // ============================================================================

  getRoles(): Observable<RoleList> {
    return this.http.get<RoleList>(`${this.baseUrl}/admin/roles`);
  }

  getRole(id: string): Observable<Role> {
    return this.http.get<Role>(`${this.baseUrl}/admin/roles/${id}`);
  }

  createRole(request: CreateRoleRequest): Observable<Role> {
    return this.http.post<Role>(`${this.baseUrl}/admin/roles`, request);
  }

  updateRole(id: string, request: UpdateRoleRequest): Observable<Role> {
    return this.http.patch<Role>(`${this.baseUrl}/admin/roles/${id}`, request);
  }

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/roles/${id}`);
  }

  // ============================================================================
  // OAuth Client Management
  // ============================================================================

  getOAuthClients(params?: PaginationParams): Observable<OAuthClientList> {
    let httpParams = new HttpParams();
    if (params) {
      if (params.page !== undefined) {
        httpParams = httpParams.set('page', params.page.toString());
      }
      if (params.pageSize !== undefined) {
        httpParams = httpParams.set('pageSize', params.pageSize.toString());
      }
    }
    return this.http.get<OAuthClientList>(`${this.baseUrl}/admin/oauth/clients`, {
      params: httpParams,
    });
  }

  getOAuthClient(id: string): Observable<OAuthClient> {
    return this.http.get<OAuthClient>(`${this.baseUrl}/admin/oauth/clients/${id}`);
  }

  createOAuthClient(request: CreateOAuthClientRequest): Observable<OAuthClientCreated> {
    return this.http.post<OAuthClientCreated>(`${this.baseUrl}/admin/oauth/clients`, request);
  }

  updateOAuthClient(id: string, request: UpdateOAuthClientRequest): Observable<OAuthClient> {
    return this.http.put<OAuthClient>(`${this.baseUrl}/admin/oauth/clients/${id}`, request);
  }

  deleteOAuthClient(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/oauth/clients/${id}`);
  }

  regenerateClientSecret(id: string): Observable<ClientSecret> {
    return this.http.post<ClientSecret>(
      `${this.baseUrl}/admin/oauth/clients/${id}/regenerate-secret`,
      {}
    );
  }

  // ============================================================================
  // OAuth Scope Management
  // ============================================================================

  getOAuthScopes(): Observable<OAuthScopeList> {
    return this.http.get<OAuthScopeList>(`${this.baseUrl}/admin/oauth/scopes`);
  }

  getOAuthScope(id: string): Observable<OAuthScope> {
    return this.http.get<OAuthScope>(`${this.baseUrl}/admin/oauth/scopes/${id}`);
  }

  createOAuthScope(request: CreateOAuthScopeRequest): Observable<OAuthScope> {
    return this.http.post<OAuthScope>(`${this.baseUrl}/admin/oauth/scopes`, request);
  }

  updateOAuthScope(id: string, request: UpdateOAuthScopeRequest): Observable<OAuthScope> {
    return this.http.put<OAuthScope>(`${this.baseUrl}/admin/oauth/scopes/${id}`, request);
  }

  deleteOAuthScope(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/oauth/scopes/${id}`);
  }

  // ============================================================================
  // OAuth API Management
  // ============================================================================

  getOAuthApis(params?: PaginationParams): Observable<OAuthApiList> {
    let httpParams = new HttpParams();
    if (params) {
      if (params.page !== undefined) {
        httpParams = httpParams.set('page', params.page.toString());
      }
      if (params.pageSize !== undefined) {
        httpParams = httpParams.set('pageSize', params.pageSize.toString());
      }
    }
    return this.http.get<OAuthApiList>(`${this.baseUrl}/admin/oauth/apis`, {
      params: httpParams,
    });
  }

  getOAuthApi(id: string): Observable<OAuthApi> {
    return this.http.get<OAuthApi>(`${this.baseUrl}/admin/oauth/apis/${id}`);
  }

  createOAuthApi(request: CreateOAuthApiRequest): Observable<OAuthApiCreated> {
    return this.http.post<OAuthApiCreated>(`${this.baseUrl}/admin/oauth/apis`, request);
  }

  updateOAuthApi(id: string, request: UpdateOAuthApiRequest): Observable<OAuthApi> {
    return this.http.put<OAuthApi>(`${this.baseUrl}/admin/oauth/apis/${id}`, request);
  }

  deleteOAuthApi(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/oauth/apis/${id}`);
  }

  regenerateApiSecret(id: string): Observable<ApiSecret> {
    return this.http.post<ApiSecret>(
      `${this.baseUrl}/admin/oauth/apis/${id}/regenerate-secret`,
      {}
    );
  }

  // ============================================================================
  // Realm Management (System Realm Only)
  // ============================================================================

  getRealms(): Observable<RealmList> {
    return this.http.get<RealmList>(`${this.baseUrl}/admin/realms`);
  }

  getRealm(slug: string): Observable<Realm> {
    return this.http.get<Realm>(`${this.baseUrl}/admin/realms/${slug}`);
  }

  createRealm(request: CreateRealmRequest): Observable<Realm> {
    return this.http.post<Realm>(`${this.baseUrl}/admin/realms`, request);
  }

  updateRealm(slug: string, request: UpdateRealmRequest): Observable<Realm> {
    return this.http.patch<Realm>(`${this.baseUrl}/admin/realms/${slug}`, request);
  }

  deleteRealm(slug: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/admin/realms/${slug}`);
  }
}
