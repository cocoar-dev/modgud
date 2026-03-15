import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';
import { router } from '@/router';
import type { CurrentUser, LoginRequest, LoginResult } from '@/core/models/auth.models';

export type AuthStatus = 'initial' | 'loading' | 'authenticated' | 'unauthenticated' | 'requires-2fa';

export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<CurrentUser | null>(null);
  const status = ref<AuthStatus>('initial');
  const error = ref<string | null>(null);

  const isAuthenticated = computed(
    () => status.value === 'authenticated' && currentUser.value !== null,
  );
  const isLoading = computed(() => status.value === 'loading');
  const isAdmin = computed(() => currentUser.value?.roles?.includes('Admin') ?? false);
  const requiresTwoFactor = computed(() => status.value === 'requires-2fa');

  const displayName = computed(() => {
    const user = currentUser.value;
    if (!user) return '';
    if (user.firstName && user.lastName) return `${user.firstName} ${user.lastName}`;
    return user.userName;
  });

  let _initPromise: Promise<void> | null = null;

  async function initialize(): Promise<void> {
    if (status.value === 'authenticated' || status.value === 'unauthenticated') return;
    if (_initPromise) return _initPromise;
    status.value = 'loading';
    error.value = null;
    _initPromise = (async () => {
      try {
        const user = await authApi.getCurrentUser();
        currentUser.value = user;
        status.value = 'authenticated';
      } catch {
        currentUser.value = null;
        status.value = 'unauthenticated';
      } finally {
        _initPromise = null;
      }
    })();
    return _initPromise;
  }

  async function login(
    request: LoginRequest,
    options?: { redirectTo?: string },
  ): Promise<LoginResult> {
    status.value = 'loading';
    error.value = null;
    try {
      const result = await authApi.login(request);
      if (result.succeeded) {
        await fetchCurrentUser(options?.redirectTo ?? '/');
      } else if (result.requiresTwoFactor) {
        status.value = 'requires-2fa';
      } else {
        status.value = 'unauthenticated';
        if (result.isLockedOut) {
          error.value = 'Your account has been temporarily locked due to multiple failed attempts. Please try again later or reset your password.';
        } else if (result.isNotAllowed) {
          error.value = 'Your account is not allowed to sign in. Please confirm your email address or contact support.';
        } else {
          error.value = 'Invalid username or password.';
        }
      }
      return result;
    } catch (err) {
      status.value = 'unauthenticated';
      error.value =
        err instanceof ApiError
          ? err.message
          : 'Login failed. Please try again.';
      return {
        succeeded: false,
        requiresTwoFactor: false,
        isLockedOut: false,
        isNotAllowed: false,
        errorMessage: error.value ?? undefined,
      };
    }
  }

  async function completeTwoFactorLogin(redirectTo?: string): Promise<void> {
    await fetchCurrentUser(redirectTo ?? '/');
  }

  async function logout(redirectTo?: string): Promise<void> {
    status.value = 'loading';
    try {
      await authApi.logout();
    } finally {
      currentUser.value = null;
      status.value = 'unauthenticated';
      error.value = null;
      router.push(redirectTo ?? '/login');
    }
  }

  async function refreshUser(): Promise<void> {
    if (status.value !== 'authenticated') return;
    try {
      currentUser.value = await authApi.getCurrentUser();
    } catch {
      currentUser.value = null;
      status.value = 'unauthenticated';
    }
  }

  function clearError(): void {
    error.value = null;
  }

  function setError(message: string): void {
    error.value = message;
  }

  async function fetchCurrentUser(redirectTo: string): Promise<void> {
    try {
      currentUser.value = await authApi.getCurrentUser();
      status.value = 'authenticated';
      router.push(redirectTo);
    } catch {
      status.value = 'unauthenticated';
      error.value = 'Failed to fetch user information.';
    }
  }

  return {
    currentUser,
    status,
    error,
    isAuthenticated,
    isLoading,
    isAdmin,
    requiresTwoFactor,
    displayName,
    initialize,
    login,
    completeTwoFactorLogin,
    logout,
    refreshUser,
    clearError,
    setError,
  };
});
