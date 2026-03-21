export type LoginProviderType = 'Internal' | 'OpenIdConnect';

export interface LoginProviderDto {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  type: LoginProviderType;
  configuration: Record<string, string>;
  isBuiltIn: boolean;
  createdAt: string;
  modifiedAt?: string;
}

export interface LoginProviderListDto {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  type: LoginProviderType;
}

export interface CreateLoginProviderDto {
  name: string;
  displayName?: string;
  description?: string;
  type: LoginProviderType;
  configuration?: Record<string, string>;
}

export interface UpdateLoginProviderDto {
  displayName?: string;
  description?: string | null;
  configuration?: Record<string, string> | null;
}

export interface LoginProviderList {
  items: LoginProviderListDto[];
  totalCount: number;
}
