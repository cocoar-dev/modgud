import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  CreateAdminRequest,
  CurrentUserDto,
  ForgotPasswordRequest,
  LoginResult,
  RegisterRequest,
  ResendConfirmationRequest,
  ResetPasswordRequest,
  SetupStatus,
} from '@/models/auth'

/**
 * Auth store — manages the current session, fetches the principal from
 * `/api/auth/me`, and exposes ABAC permission checks.
 *
 * `hasPermission(permission)` returns true if the user has
 *   `system:admin` OR `tenant:admin` OR the specific permission string.
 */
export const useAuthStore = defineStore('auth', () => {
  const http = useHttpClient('/api/auth')
  const setupHttp = useHttpClient('/api/setup')

  const user = ref<CurrentUserDto | null>(null)

  const isAuthenticated = computed(() => user.value !== null)
  const permissions = computed(() => user.value?.Permissions ?? [])
  const roles = computed(() => user.value?.Roles ?? [])

  /** System or tenant admin — used to gate top-level admin sections. */
  const isAdmin = computed(
    () =>
      permissions.value.includes('system:admin') ||
      permissions.value.includes('tenant:admin') ||
      roles.value.includes('Admin'),
  )

  const displayName = computed(() => {
    const u = user.value
    if (!u) return ''
    if (u.FirstName && u.LastName) return `${u.FirstName} ${u.LastName}`
    return u.UserName
  })

  /**
   * ABAC capability check. Admins (system or tenant) bypass all checks.
   */
  function hasPermission(permission: string): boolean {
    const perms = permissions.value
    if (perms.includes('system:admin')) return true
    if (perms.includes('tenant:admin')) return true
    return perms.includes(permission)
  }

  /**
   * Login with username + password. On 2FA or lockout the result is
   * returned so the caller can redirect; otherwise `fetchMe()` is called.
   */
  async function login(
    userName: string,
    password: string,
    rememberMe = false,
  ): Promise<LoginResult> {
    const result = await http
      .addPath('login')
      .post<LoginResult>({ UserName: userName, Password: password, RememberMe: rememberMe })

    if (result?.Succeeded) {
      await fetchMe()
    }
    return result
  }

  async function logout(): Promise<void> {
    try {
      await http.addPath('logout').post()
    } finally {
      user.value = null
    }
  }

  async function register(data: RegisterRequest): Promise<void> {
    await http.addPath('register').post(data)
  }

  async function forgotPassword(data: ForgotPasswordRequest): Promise<void> {
    await http.addPath('forgot-password').post(data)
  }

  async function resetPassword(data: ResetPasswordRequest): Promise<void> {
    await http.addPath('reset-password').post(data)
  }

  async function resendConfirmation(data: ResendConfirmationRequest): Promise<void> {
    await http.addPath('resend-confirmation').post(data)
  }

  async function confirmEmail(userId: string, token: string): Promise<void> {
    await http
      .addPath('confirm-email')
      .setQueryParameter('userId', userId)
      .setQueryParameter('token', token)
      .get()
  }

  async function fetchMe(): Promise<boolean> {
    try {
      user.value = await http.addPath('me').get<CurrentUserDto>()
      return true
    } catch {
      user.value = null
      return false
    }
  }

  async function fetchSetupStatus(): Promise<SetupStatus> {
    return await setupHttp.addPath('status').get<SetupStatus>()
  }

  async function createAdmin(data: CreateAdminRequest): Promise<void> {
    await setupHttp.addPath('create-admin').post(data)
    await fetchMe()
  }

  return {
    user,
    isAuthenticated,
    isAdmin,
    permissions,
    roles,
    displayName,
    hasPermission,
    login,
    logout,
    register,
    forgotPassword,
    resetPassword,
    resendConfirmation,
    confirmEmail,
    fetchMe,
    fetchSetupStatus,
    createAdmin,
  }
})
