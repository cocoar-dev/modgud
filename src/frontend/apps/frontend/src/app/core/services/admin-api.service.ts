import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
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
} from '../models/auth.models';

@Injectable({
  providedIn: 'root',
})
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl;

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
}
