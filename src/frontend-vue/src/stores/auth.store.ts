import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import type { AuthUser, EmailOtpStatus, LoginResponse } from '@/models/auth'

// Shape of the DataEvent the UserHub broadcasts — same wire format as
// useEntityService uses. We only need Action + Payload here; Payload items
// for "Created"/"Updated" carry UserDto (PascalCase), with Id as the first
// field we match against.
interface UserHubEvent {
  Action: 'Created' | 'Updated' | 'Deleted' | 'FullSync'
  Subject: string
  Payload: Array<{ Id?: string } | string>
}

export const useAuthStore = defineStore('auth', () => {
  const http = useHttpClient('/api/account')
  const emailOtpHttp = useHttpClient('/api/account/email-otp')
  const magicLinkHttp = useHttpClient('/api/account/magic-link')
  const signalr = useSignalR()

  const user = ref<AuthUser | null>(null)
  const isAuthenticated = computed(() => user.value !== null)
  const permissions = computed(() => user.value?.Permissions ?? [])

  // Live-refresh `user` when anything updates the current user on the server
  // (admin editing profile fields, IdP user-update-script, SCIM later, …).
  // Subscribes once on first successful fetchMe and re-subscribes on every
  // SignalR reconnect. Payload.Id from the UserHub is the same ShortGuid as
  // user.Id, so a straight string-compare is enough to filter.
  let signalrSubscribed = false
  function ensureUserUpdateSubscription() {
    if (signalrSubscribed) return
    signalrSubscribed = true
    signalr.runOnEveryReconnect(() => {
      signalr.stream<UserHubEvent>('UserActions.Subscribe').subscribe({
        next: (ev) => {
          if (ev.Action !== 'Updated' && ev.Action !== 'Created') return
          const myId = user.value?.Id
          if (!myId) return
          const match = ev.Payload.some(
            (p) => typeof p === 'object' && p !== null && 'Id' in p && p.Id === myId
          )
          if (match) {
            // Refetch /me rather than trying to merge UserDto → AuthUser —
            // the shapes diverge (AuthUser has Permissions, Has2FA, etc.)
            // and /me is the authoritative view.
            fetchMe()
          }
        },
        error: (err) => console.error('[auth.store] UserActions stream error:', err),
      })
    }, 'auth.store.UserActions.Subscribe')
  }

  /**
   * Mirrors the backend PermissionEvaluator (post-Step-4 catalog refactor).
   * Permission strings are bare 2-segment <c>"&lt;resource&gt;:&lt;action&gt;"</c>
   * (e.g. "user:read") plus the synthetic "realm:admin" entry. The App
   * context is implicit: /me returns the union of modgud and (when
   * the realm has it) control-plane grants, so all admin sidebar gates
   * and dashboard cards see a single bare-string set.
   *
   * Bypasses recognised:
   *   - realm:admin                  → realm-wide bypass
   *   - <resource>:admin             → covers every action on that resource
   *
   * Realm-management UI (control-plane resources like realm:read/write)
   * is additionally hidden on non-Control-Plane realms — the underlying
   * routing gate 404s anyway, but we hide the entries so users don't get
   * a "link worked, page is 404" experience. That extra clamp lives at
   * call sites that read the IsControlPlane flag directly.
   */
  function hasPermission(permission: string): boolean {
    const grants = permissions.value

    if (grants.includes('realm:admin')) return true
    if (grants.includes(permission)) return true

    const parts = permission.split(':')
    if (parts.length === 2) {
      if (grants.includes(`${parts[0]}:admin`)) return true
    }
    return false
  }

  /**
   * Login with username and password.
   * Returns MfaMethods if 2FA is needed, otherwise completes login.
   */
  async function login(userName: string, password: string, rememberMe: boolean = false): Promise<LoginResponse | void> {
    const result = await http.addPath('login').post<LoginResponse>({ UserName: userName, Password: password, RememberMe: rememberMe })
    if (result?.RequiresMfa) {
      // Partial sign-in only (TwoFactorUserId cookie) — /api/account/me would 401.
      // Caller (LoginView) shows the MFA-choice/code step; fetchMe runs after mfaLogin succeeds.
      return result
    }
    if (result?.RequiresSecureSetup) {
      // Full sign-in already happened on the backend (PasswordSignInAsync.Succeeded
      // before the 2FA-setup-required gate). SecureSetupModal reads authStore.user.Email
      // for the Email-OTP option, so we MUST populate the store now — otherwise the
      // modal renders the "no email configured" branch even though one is on file.
      await fetchMe()
      return result
    }
    await fetchMe()
  }

  /**
   * Complete 2FA login with TOTP code.
   */
  async function mfaLogin(code: string, rememberMe: boolean = false): Promise<void> {
    await http.addPath('mfa', 'login').post({ Code: code, RememberMe: rememberMe })
    await fetchMe()
  }

  /**
   * Request Email OTP code (sends email). Call during 2FA flow.
   */
  async function requestEmailOtp(): Promise<void> {
    await emailOtpHttp.addPath('login', 'request').post({})
  }

  /**
   * Complete 2FA login with Email OTP code.
   */
  async function emailOtpLogin(code: string, rememberMe: boolean = false): Promise<void> {
    await emailOtpHttp.addPath('login').post({ Code: code, RememberMe: rememberMe })
    await fetchMe()
  }

  /**
   * Get Email OTP status for current user (profile page).
   */
  async function getEmailOtpStatus(): Promise<EmailOtpStatus> {
    return await emailOtpHttp.addPath('status').get<EmailOtpStatus>()
  }

  /**
   * Enable Email OTP for current user.
   */
  async function enableEmailOtp(): Promise<void> {
    await emailOtpHttp.addPath('enable').post({})
  }

  /**
   * Disable Email OTP for current user.
   */
  async function disableEmailOtp(): Promise<void> {
    await emailOtpHttp.addPath('disable').post({})
  }

  /**
   * Request a magic link email for passwordless login. `returnUrl` threads a
   * pending post-login continuation (e.g. a /connect/authorize OIDC flow)
   * through the e-mail round trip — the backend appends it to the emailed
   * /magic-login URL as ?redirect= after validating it server-side.
   */
  async function requestMagicLink(email: string, returnUrl?: string): Promise<void> {
    await magicLinkHttp.addPath('request').post({ Email: email, ReturnUrl: returnUrl ?? null })
  }

  /**
   * Complete login via magic link token. When the account has TOTP enabled the
   * backend does NOT grant a full session — it returns RequiresMfa and sets the
   * partial-2FA cookie, so the caller (MagicLoginView) must collect the TOTP
   * code and finish via mfaLogin. Magic-link is no longer a 2FA bypass.
   */
  async function magicLinkLogin(userId: string, token: string, rememberMe: boolean = false): Promise<LoginResponse | void> {
    const result = await magicLinkHttp.addPath('login').post<LoginResponse>({ UserId: userId, Token: token, RememberMe: rememberMe })
    if (result?.RequiresMfa) {
      // Partial sign-in only — /api/account/me would 401 until TOTP completes.
      return result
    }
    await fetchMe()
  }

  /**
   * Logout. For federated sessions, `endIdpSession` controls whether the
   * IdP-side session is also ended (RP-initiated logout). Default `true`
   * keeps the existing behavior for non-federated callers that don't care.
   *
   * Always ends with a full-page navigation rather than SPA routing — this
   * drops the SignalR singleton, clears Pinia state, and guarantees the next
   * request re-authenticates from scratch. Critical for security when
   * permissions change mid-session.
   */
  async function logout(endIdpSession: boolean = true): Promise<void> {
    const response = await http.addPath('logout').post<{ Message: string; ExternalLogoutUrl?: string | null }>({ EndIdpSession: endIdpSession })
    user.value = null
    const target = response?.ExternalLogoutUrl ?? '/login'
    window.location.assign(target)
  }

  async function fetchMe(): Promise<boolean> {
    try {
      user.value = await http.addPath('me').get<AuthUser>()
      // Bring SignalR up *after* we know the session cookie is valid. The
      // connection hub requires auth; starting it before login just earns a
      // 401 on /negotiate and leaves the indicator stuck in red.
      signalr.connect()
      // Subscribe to live updates the first time we know who "me" is.
      ensureUserUpdateSubscription()
      return true
    } catch {
      user.value = null
      return false
    }
  }

  return {
    user,
    isAuthenticated,
    permissions,
    hasPermission,
    login,
    mfaLogin,
    requestEmailOtp,
    emailOtpLogin,
    getEmailOtpStatus,
    enableEmailOtp,
    disableEmailOtp,
    requestMagicLink,
    magicLinkLogin,
    logout,
    fetchMe,
  }
})
