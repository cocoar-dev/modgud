export type LoginProviderType = 'Internal' | 'OpenIdConnect';

export interface LoginProviderDto {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  type: LoginProviderType;
  configuration?: string;
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
  configuration?: string;
}

export interface UpdateLoginProviderDto {
  displayName?: string;
  description?: string | null;
  configuration?: string | null;
}

export interface LoginProviderList {
  items: LoginProviderListDto[];
  totalCount: number;
}
