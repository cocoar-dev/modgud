import { http } from './http';
import type {
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
  Group,
  GroupDetail,
  GroupList,
  CreateGroupRequest,
  UpdateGroupRequest,
} from '../models/auth.models';
import type {
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
  CreateApiSecretRequest,
  ApiSecretCreated,
} from '../models/oauth.models';
import type {
  LoginProviderDto,
  LoginProviderList,
  CreateLoginProviderDto,
  UpdateLoginProviderDto,
} from '../models/login-provider.models';

function buildQuery(params?: PaginationParams): string {
  if (!params) return '';
  const q = new URLSearchParams();
  if (params.page !== undefined) q.set('page', String(params.page));
  if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize));
  if (params.search) q.set('search', params.search);
  if (params.sortBy) q.set('sortBy', params.sortBy);
  if (params.sortDescending !== undefined) q.set('sortDescending', String(params.sortDescending));
  const s = q.toString();
  return s ? `?${s}` : '';
}

export const adminApi = {
  // Users
  getUsers: (params?: PaginationParams) => http.get<UserList>(`/admin/users${buildQuery(params)}`),
  getUser: (id: string) => http.get<User>(`/admin/users/${id}`),
  createUser: (req: CreateUserRequest) => http.post<User>('/admin/users', req),
  updateUser: (id: string, req: UpdateUserRequest) => http.patch<User>(`/admin/users/${id}`, req),
  deleteUser: (id: string) => http.delete<void>(`/admin/users/${id}`),
  resetUserPassword: (id: string, req: AdminResetPasswordRequest) =>
    http.post<void>(`/admin/users/${id}/reset-password`, req),
  unlockUser: (id: string) => http.post<void>(`/admin/users/${id}/unlock`, {}),
  getUserSessions: (id: string) => http.get<SessionList>(`/admin/users/${id}/sessions`),
  revokeUserSessions: (id: string) => http.delete<void>(`/admin/users/${id}/sessions`),
  softDeleteUser: (id: string, req?: AdminSoftDeleteRequest) =>
    http.post<void>(`/admin/users/${id}/soft-delete`, req ?? {}),
  restoreUser: (id: string, req?: AdminRestoreRequest) =>
    http.post<void>(`/admin/users/${id}/restore`, req ?? {}),
  permanentlyEraseUser: (id: string, req: AdminPermanentEraseRequest) =>
    http.delete<void>(`/admin/users/${id}/permanent`, req),
  getDeletionStatus: (id: string) => http.get<DeletionStatus>(`/admin/users/${id}/deletion-status`),

  // Roles
  getRoles: () => http.get<RoleList>('/admin/roles'),
  getRole: (id: string) => http.get<Role>(`/admin/roles/${id}`),
  createRole: (req: CreateRoleRequest) => http.post<Role>('/admin/roles', req),
  updateRole: (id: string, req: UpdateRoleRequest) => http.patch<Role>(`/admin/roles/${id}`, req),
  deleteRole: (id: string) => http.delete<void>(`/admin/roles/${id}`),

  // OAuth Clients
  getOAuthClients: (params?: PaginationParams) =>
    http.get<OAuthClientList>(`/admin/oauth/clients${buildQuery(params)}`),
  getOAuthClient: (id: string) => http.get<OAuthClient>(`/admin/oauth/clients/${id}`),
  createOAuthClient: (req: CreateOAuthClientRequest) =>
    http.post<OAuthClientCreated>('/admin/oauth/clients', req),
  updateOAuthClient: (id: string, req: UpdateOAuthClientRequest) =>
    http.put<OAuthClient>(`/admin/oauth/clients/${id}`, req),
  deleteOAuthClient: (id: string) => http.delete<void>(`/admin/oauth/clients/${id}`),
  regenerateClientSecret: (id: string) =>
    http.post<ClientSecret>(`/admin/oauth/clients/${id}/regenerate-secret`, {}),

  // OAuth Scopes
  getOAuthScopes: () => http.get<OAuthScopeList>('/admin/oauth/scopes'),
  getOAuthScope: (id: string) => http.get<OAuthScope>(`/admin/oauth/scopes/${id}`),
  createOAuthScope: (req: CreateOAuthScopeRequest) =>
    http.post<OAuthScope>('/admin/oauth/scopes', req),
  updateOAuthScope: (id: string, req: UpdateOAuthScopeRequest) =>
    http.put<OAuthScope>(`/admin/oauth/scopes/${id}`, req),
  deleteOAuthScope: (id: string) => http.delete<void>(`/admin/oauth/scopes/${id}`),

  // OAuth APIs
  getOAuthApis: (params?: PaginationParams) =>
    http.get<OAuthApiList>(`/admin/oauth/apis${buildQuery(params)}`),
  getOAuthApi: (id: string) =>
    http.get<OAuthApi>(`/admin/oauth/apis/${id}`),
  createOAuthApi: (req: CreateOAuthApiRequest) =>
    http.post<OAuthApiCreated>('/admin/oauth/apis', req),
  updateOAuthApi: (id: string, req: UpdateOAuthApiRequest) =>
    http.put<OAuthApi>(`/admin/oauth/apis/${id}`, req),
  deleteOAuthApi: (id: string) =>
    http.delete<void>(`/admin/oauth/apis/${id}`),
  regenerateApiSecret: (id: string) =>
    http.post<ApiSecret>(`/admin/oauth/apis/${id}/regenerate-secret`, {}),
  createApiSecret: (id: string, req: CreateApiSecretRequest) =>
    http.post<ApiSecretCreated>(`/admin/oauth/apis/${id}/secrets`, req),
  deleteApiSecret: (id: string, secretId: string) =>
    http.delete<void>(`/admin/oauth/apis/${id}/secrets/${secretId}`),

  // Realms (System Realm Only)
  getRealms: () => http.get<RealmList>('/admin/realms'),
  getRealm: (slug: string) => http.get<Realm>(`/admin/realms/${slug}`),
  createRealm: (req: CreateRealmRequest) => http.post<Realm>('/admin/realms', req),
  updateRealm: (slug: string, req: UpdateRealmRequest) => http.patch<Realm>(`/admin/realms/${slug}`, req),
  deleteRealm: (slug: string) => http.delete<void>(`/admin/realms/${slug}`),

  // Login Providers
  getLoginProviders: () => http.get<LoginProviderList>('/admin/login-providers'),
  getLoginProvider: (id: string) => http.get<LoginProviderDto>(`/admin/login-providers/${id}`),
  createLoginProvider: (req: CreateLoginProviderDto) =>
    http.post<LoginProviderDto>('/admin/login-providers', req),
  updateLoginProvider: (id: string, req: UpdateLoginProviderDto) =>
    http.put<void>(`/admin/login-providers/${id}`, req),
  deleteLoginProvider: (id: string) => http.delete<void>(`/admin/login-providers/${id}`),

  // Groups
  getGroups: () => http.get<GroupList>('/admin/groups'),
  getGroup: (id: string) => http.get<GroupDetail>(`/admin/groups/${id}`),
  createGroup: (req: CreateGroupRequest) => http.post<GroupDetail>('/admin/groups', req),
  updateGroup: (id: string, req: UpdateGroupRequest) => http.patch<void>(`/admin/groups/${id}`, req),
  deleteGroup: (id: string) => http.delete<void>(`/admin/groups/${id}`),
  addGroupMember: (id: string, userId: string) => http.post<void>(`/admin/groups/${id}/members`, { userId }),
  removeGroupMember: (id: string, userId: string) => http.delete<void>(`/admin/groups/${id}/members/${userId}`),
  addChildGroup: (id: string, childGroupId: string) => http.post<void>(`/admin/groups/${id}/children`, { childGroupId }),
  removeChildGroup: (id: string, childId: string) => http.delete<void>(`/admin/groups/${id}/children/${childId}`),
  grantGroupRole: (id: string, roleId: string, clientId?: string) =>
    http.post<void>(`/admin/groups/${id}/roles`, { roleId, clientId: clientId || undefined }),
  revokeGroupRole: (id: string, roleId: string, clientId?: string) =>
    http.delete<void>(`/admin/groups/${id}/roles/${roleId}${clientId ? `?clientId=${clientId}` : ''}`),

};
