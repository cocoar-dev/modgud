<script setup lang="ts">
import { ref, onMounted, watch, computed } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { usePreferences, localeOptions } from '@/composables/usePreferences'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon, CoarMenu, CoarMenuItem, CoarSelect, CoarTextInput, CoarFormField, CoarNote } from '@cocoar/vue-ui'
import type { CoarSelectOption } from '@cocoar/vue-ui'
import { useAppConfigStore } from '@/stores/appconfig.store'
import MfaSetupModal from '../auth/MfaSetupModal.vue'
import ChangePasswordModal from './ChangePasswordModal.vue'

const { t, language } = useI18n()
const router = useRouter()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()
const { darkMode, setDarkMode, setLocale } = usePreferences()
const mfaHttp = useHttpClient('/api/account/mfa')
const passkeyHttp = useHttpClient('/api/account/passkey')
const profileHttp = useHttpClient('/api/account/profile')

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('profile.title', {}, 'Profile')
  ctx.header.icon = 'user'
  ctx.content.container = false
}), { immediate: true })

// Navigation
const activeSection = ref<'account' | 'security' | 'sessions' | 'privacy' | 'preferences'>('account')

// ─── Sessions self-service ────────────────────────────────────────────────
import type { SessionDto, SessionListDto } from '@/models/session'
const sessionsHttp = useHttpClient('/api/auth/sessions')
const sessions = ref<SessionDto[]>([])
const sessionsLoading = ref(false)
const sessionsError = ref('')
const revokingSessionId = ref<string | null>(null)
const revokingAll = ref(false)

async function loadSessions() {
  if (sessionsLoading.value) return
  sessionsLoading.value = true
  sessionsError.value = ''
  try {
    const res = await sessionsHttp.get<SessionListDto>()
    sessions.value = res.Sessions ?? []
  } catch (e: any) {
    sessionsError.value = e?.message ?? String(e)
  } finally {
    sessionsLoading.value = false
  }
}

async function revokeSession(id: string) {
  if (!confirm(t('profile.sessions.confirmRevoke', {}, 'Diese Sitzung wirklich beenden?'))) return
  revokingSessionId.value = id
  try {
    await sessionsHttp.addPath(id).delete()
    sessions.value = sessions.value.filter((s) => s.Id !== id)
  } catch (e: any) {
    sessionsError.value = e?.message ?? String(e)
  } finally {
    revokingSessionId.value = null
  }
}

async function revokeAllSessions() {
  if (!confirm(t('profile.sessions.confirmRevokeAll', {}, 'Wirklich überall abmelden? Du wirst neu angemeldet.'))) return
  revokingAll.value = true
  try {
    await sessionsHttp.delete()
    // Best-effort UX — backend dropped all our sessions, including the current one.
    // Force a hard redirect to /login so the cleared cookie is honored.
    window.location.assign('/login')
  } catch (e: any) {
    sessionsError.value = e?.message ?? String(e)
    revokingAll.value = false
  }
}

// ─── GDPR / Privacy self-service ──────────────────────────────────────────
import type { DeletionStatusDto } from '@/models/gdpr'
const gdprHttp = useHttpClient('/api/auth')
const deletionStatus = ref<DeletionStatusDto | null>(null)
const exportRunning = ref(false)
const deleteRequestRunning = ref(false)
const deleteCancelRunning = ref(false)
const deletePassword = ref('')
const deleteReason = ref('')
const showDeleteForm = ref(false)
const privacyError = ref('')
const privacyMessage = ref('')

async function loadDeletionStatus() {
  privacyError.value = ''
  try {
    deletionStatus.value = await gdprHttp.addPath('deletion-status').get<DeletionStatusDto>()
  } catch (e: any) {
    privacyError.value = e?.message ?? String(e)
  }
}

async function exportMyData() {
  if (exportRunning.value) return
  exportRunning.value = true
  privacyError.value = ''
  try {
    // Fetch the JSON dump and trigger a browser download — keeping the user on the page.
    const res = await fetch('/api/auth/export-data', { credentials: 'include' })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `cocoar-auth-export-${new Date().toISOString().slice(0, 10)}.json`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch (e: any) {
    privacyError.value = e?.message ?? String(e)
  } finally {
    exportRunning.value = false
  }
}

async function requestDeletion() {
  if (deleteRequestRunning.value) return
  if (!deletePassword.value) {
    privacyError.value = t('profile.privacy.passwordRequired', {}, 'Passwort ist erforderlich.')
    return
  }
  deleteRequestRunning.value = true
  privacyError.value = ''
  privacyMessage.value = ''
  try {
    await gdprHttp.addPath('delete-account').post({
      Password: deletePassword.value,
      Reason: deleteReason.value.trim() || null,
    })
    privacyMessage.value = t('profile.privacy.deleteRequested', {},
      'Bestätigungs-Mail wurde gesendet. Bitte über den Link in der Mail bestätigen.')
    deletePassword.value = ''
    deleteReason.value = ''
    showDeleteForm.value = false
    await loadDeletionStatus()
  } catch (e: any) {
    privacyError.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    deleteRequestRunning.value = false
  }
}

async function cancelDeletion() {
  if (deleteCancelRunning.value) return
  if (!confirm(t('profile.privacy.confirmCancel', {}, 'Löschanfrage zurückziehen?'))) return
  deleteCancelRunning.value = true
  privacyError.value = ''
  try {
    await gdprHttp.addPath('cancel-deletion').post({})
    privacyMessage.value = t('profile.privacy.cancelled', {}, 'Löschanfrage zurückgezogen.')
    await loadDeletionStatus()
  } catch (e: any) {
    privacyError.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    deleteCancelRunning.value = false
  }
}

function deviceIcon(s: SessionDto): string {
  const dt = (s.DeviceType ?? '').toLowerCase()
  if (dt.includes('mobile') || dt.includes('phone')) return 'smartphone'
  if (dt.includes('tablet')) return 'tablet'
  return 'monitor'
}

function deviceLabel(s: SessionDto): string {
  const browser = [s.Browser, s.BrowserVersion].filter(Boolean).join(' ')
  const os = [s.OperatingSystem, s.OsVersion].filter(Boolean).join(' ')
  return [browser, os].filter(Boolean).join(' · ') || (s.DeviceType ?? t('profile.sessions.unknownDevice', {}, 'Unbekanntes Gerät'))
}

// MFA state
const mfaStatus = ref<{ Enabled: boolean } | null>(null)
const showMfaSetup = ref(false)
const showChangePassword = ref(false)
const disabling = ref(false)

// Email OTP state
const emailOtpStatus = ref<{ Enabled: boolean; HasEmail: boolean } | null>(null)
const emailOtpToggling = ref(false)

// Passkey state
const passkeys = ref<{ Id: string; DisplayName: string; CreatedAt: string; LastUsedAt: string | null }[]>([])
const passkeyRegistering = ref(false)
const passkeyError = ref('')

// External-identity links state (Phase 7)
import type { ExternalLinkDto } from '@/models/externalLink'
interface AvailableIdp { Id: string; DisplayName: string; Flavor: string; IconName?: string | null; ButtonColorHex?: string | null }
const externalLinks = ref<ExternalLinkDto[]>([])
const availableIdps = ref<AvailableIdp[]>([])
const linksError = ref('')
const expandedLinks = ref<Set<string>>(new Set())
const linksHttp = useHttpClient('/api/account/external-links')

function toggleLinkExpand(linkId: string) {
  const next = new Set(expandedLinks.value)
  if (next.has(linkId)) next.delete(linkId)
  else next.add(linkId)
  expandedLinks.value = next
}

async function loadExternalLinks() {
  try {
    externalLinks.value = await linksHttp.get<ExternalLinkDto[]>()
  } catch (e: any) {
    linksError.value = e?.message ?? String(e)
  }
}

async function loadAvailableIdps() {
  try {
    const res = await fetch('/api/account/external-logins')
    if (res.ok) availableIdps.value = await res.json()
  } catch { /* ignore */ }
}

function linkWith(loginProviderId: string) {
  // Same start-flow as login; the finish endpoint detects the active app
  // cookie and routes the external identity into "link to current user".
  window.location.href = `/api/account/external-login/${loginProviderId}/start?returnUrl=/profile`
}

async function unlink(linkId: string, displayName: string) {
  if (!confirm(t('profile.externalLinks.confirmUnlink', {}, 'Disconnect ') + displayName + '?')) return
  try {
    await linksHttp.addPath(linkId).delete()
    externalLinks.value = externalLinks.value.filter(l => l.Id !== linkId)
  } catch (e: any) {
    linksError.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  }
}

// IdPs that are enabled but not yet linked — shown as "Link with X" buttons.
const unlinkedIdps = computed(() => {
  const linkedIds = new Set(externalLinks.value.map(l => l.LoginProviderId))
  return availableIdps.value.filter(idp => !linkedIds.has(idp.Id))
})

// Profile editable form — every change goes via the aggregate change-request.
interface ProfileForm { Firstname: string; Lastname: string; Acronym: string; Email: string }
const profileForm = ref<ProfileForm>({ Firstname: '', Lastname: '', Acronym: '', Email: '' })
// The seed tracks the values the form was last reset to, so "dirty" means the user
// actually typed something since then — not just "the input matches or differs from the
// persisted user value". Without this, pending changes would be silently wiped out when
// the user saved an unrelated edit, because the untouched fields would match the user
// state and be interpreted as "please drop these pending values".
const formSeed = ref<ProfileForm>({ Firstname: '', Lastname: '', Acronym: '', Email: '' })
const profileSaving = ref(false)
const profileSavedHint = ref(false)
const profileError = ref('')

function seedProfileForm() {
  const seeded: ProfileForm = {
    Firstname: authStore.user?.Firstname || '',
    Lastname: authStore.user?.Lastname || '',
    Acronym: authStore.user?.Acronym || '',
    Email: authStore.user?.Email || '',
  }
  profileForm.value = { ...seeded }
  formSeed.value = { ...seeded }
}

interface ChangeItem { Field: string; OldValue: string | null; NewValue: string | null }
interface OpenRequest {
  Id: string
  Status: 'EmailVerificationPending' | 'AdminApprovalPending'
  Changes: ChangeItem[]
  RequestedAt: string
  UpdatedAt: string
  VerifiedAt: string | null
}
interface TerminalRequest {
  Id: string
  Status: 'Approved' | 'Rejected'
  Changes: ChangeItem[]
  RequestedAt: string
  ReviewedAt: string | null
  ReviewerNote: string | null
}

const openRequest = ref<OpenRequest | null>(null)
const lastTerminal = ref<TerminalRequest | null>(null)

const profileDirty = computed(() => {
  const f = profileForm.value
  const s = formSeed.value
  return f.Firstname !== s.Firstname
    || f.Lastname  !== s.Lastname
    || f.Acronym   !== s.Acronym
    || f.Email     !== s.Email
})

async function loadRequest() {
  try {
    const res = await profileHttp.addPath('request').get<{ Open: OpenRequest | null; LastTerminal: TerminalRequest | null }>()
    openRequest.value = res.Open
    lastTerminal.value = res.LastTerminal
  } catch { /* ignore */ }
}

// Sensitive actions that rely on the inbox (profile change-request,
// Email-OTP enable) are gated on a verified email — server enforces this
// independently, UI mirrors with disabled controls + an inline hint.
const emailUnverified = computed(() => authStore.user?.EmailConfirmed === false)

async function saveProfile() {
  if (!profileDirty.value || profileSaving.value || emailUnverified.value) return
  profileSaving.value = true
  profileError.value = ''
  try {
    // Only submit fields the user actually edited in this session (differ from the seed).
    // Sending untouched fields would race with existing pending values — the server would
    // see them as "user wants this value" and, on the cleanup-if-matches-current pass,
    // wipe out previously requested changes for those fields.
    const body: Record<string, string | null> = {}
    const f = profileForm.value
    const s = formSeed.value
    if (f.Firstname !== s.Firstname) body.Firstname = f.Firstname
    if (f.Lastname  !== s.Lastname)  body.Lastname  = f.Lastname
    if (f.Acronym   !== s.Acronym)   body.Acronym   = f.Acronym
    if (f.Email     !== s.Email)     body.Email     = f.Email

    await profileHttp.addPath('request').put(body)
    await loadRequest()
    // After submit, reset the inputs to the current persisted user state — the inline
    // "Angefragt: …" hints carry the requested values. The form represents "what the user
    // wants to change next", so it naturally empties back to current once a submission lands.
    seedProfileForm()
    profileSavedHint.value = true
    setTimeout(() => profileSavedHint.value = false, 2500)
  } catch (e: any) {
    profileError.value = e?.status === 409
      ? t('profile.email.conflict', {}, 'This email address is already in use.')
      : e?.status === 400
        ? t('profile.email.invalid', {}, 'Invalid input.')
        : t('profile.email.submitFailed', {}, 'Request could not be sent.')
  } finally { profileSaving.value = false }
}

async function cancelRequest() {
  if (!openRequest.value) return
  if (!confirm(t('profile.email.confirmCancel', {}, 'Withdraw request?'))) return
  try {
    await profileHttp.addPath('request').delete()
    await loadRequest()
    seedProfileForm()
  } catch { /* ignore */ }
}


/** Returns the pending change for a given profile field, or null. */
function pendingFor(field: string): ChangeItem | null {
  return openRequest.value?.Changes.find(c => c.Field === field) ?? null
}

/** Drop a single field from the open request. Sends the user's current profile value
 *  for that field — the backend's cleanup-if-matches-current step removes it from the
 *  payload (and deletes the whole request if that was the last pending change). */
async function revertPendingField(field: 'Firstname' | 'Lastname' | 'Acronym' | 'Email') {
  if (profileSaving.value) return
  profileSaving.value = true
  profileError.value = ''
  try {
    const currentValue = authStore.user?.[field] ?? ''
    await profileHttp.addPath('request').put({ [field]: currentValue })
    await loadRequest()
    seedProfileForm()
  } catch { /* ignore */ } finally { profileSaving.value = false }
}

// Preferences
const localeSelectOptions: CoarSelectOption<string>[] = localeOptions.map(o => ({ value: o.value, label: o.label }))
const themeValue = ref(darkMode.value ? 'dark' : 'light')
watch(themeValue, (val) => setDarkMode(val === 'dark'))

function onLocaleChange(locale: string | null) {
  if (locale) setLocale(locale)
}

onMounted(() => {
  // authStore.user stays fresh via SignalR UserActions subscription
  // (see auth.store.ts) — no explicit fetch needed here.
  loadMfaStatus()
  loadEmailOtpStatus()
  loadPasskeys()
  seedProfileForm()
  loadRequest()
  loadExternalLinks()
  loadAvailableIdps()
  // Eagerly fetch deletion status — it drives the "Konto löschen" button state
  // even if the user never opens the Privacy section.
  loadDeletionStatus()
})

watch(() => authStore.user, () => seedProfileForm())

// Lazy-load sessions only when the user actually navigates to the Sessions tab.
// Same idea for the privacy/GDPR section — keeps the initial profile render
// snappy and avoids hitting the API for tabs the user never opens.
watch(activeSection, (section) => {
  if (section === 'sessions' && sessions.value.length === 0 && !sessionsLoading.value) {
    loadSessions()
  }
  if (section === 'privacy' && !deletionStatus.value) {
    loadDeletionStatus()
  }
})

async function loadMfaStatus() {
  try { mfaStatus.value = await mfaHttp.addPath('status').get() } catch { /* ignore */ }
}

// True if removing `method` would leave the user with zero 2FA methods *and*
// enforcement is active. In that case the backend will expire the grace period
// to "now" — so the next login lands on the blocking SecureSetupModal without
// a fresh window. We warn the user explicitly before sending the disable.
function isLastMethodDisable(method: 'totp' | 'email' | 'passkey'): boolean {
  if ((appConfig.config.AuthenticationMinimumLevel ?? 0) < 1) return false
  // Admin-set per-user opt-out: exempt users skip enforcement entirely, no warning needed.
  if (authStore.user?.TwoFactorExempt) return false
  const methods = authStore.user?.TwoFactorMethods ?? []
  if (method === 'passkey') {
    return methods.length === 1 && methods[0] === 'passkey' && passkeys.value.length <= 1
  }
  return methods.length === 1 && methods[0] === method
}

const lastMethodWarning = () => t(
  'profile.disable.lastMethodWarning',
  {},
  'This is your last 2FA method. After disabling, you will be required to set up 2FA on your next login (no grace period). Continue?'
)

// Forces a logout + redirect to login so the user re-enters the auth flow and
// sees the (now blocking) SecureSetupModal instead of running into 403s on the
// next API call from this stale session.
async function forceReauthAfterLastDisable() {
  await authStore.logout()
  router.push('/login')
}

async function disableMfa() {
  const isLast = isLastMethodDisable('totp')
  if (!confirm(isLast ? lastMethodWarning() : t('profile.mfa.confirmDisable', {}, 'Really disable MFA?'))) return
  disabling.value = true
  try {
    await mfaHttp.addPath('disable').post()
    mfaStatus.value = { Enabled: false }
    if (isLast) await forceReauthAfterLastDisable()
  }
  catch { /* ignore */ } finally { disabling.value = false }
}

async function loadEmailOtpStatus() {
  try { emailOtpStatus.value = await authStore.getEmailOtpStatus() } catch { /* ignore */ }
}

async function toggleEmailOtp() {
  if (emailOtpStatus.value?.Enabled) {
    const isLast = isLastMethodDisable('email')
    if (isLast && !confirm(lastMethodWarning())) return
    emailOtpToggling.value = true
    try {
      await authStore.disableEmailOtp()
      emailOtpStatus.value = { ...emailOtpStatus.value, Enabled: false }
      if (isLast) await forceReauthAfterLastDisable()
    } catch { /* ignore */ } finally { emailOtpToggling.value = false }
    return
  }
  emailOtpToggling.value = true
  try {
    await authStore.enableEmailOtp()
    emailOtpStatus.value = { Enabled: true, HasEmail: true }
  } catch { /* ignore */ } finally { emailOtpToggling.value = false }
}

async function loadPasskeys() {
  try { passkeys.value = await passkeyHttp.get() } catch { /* ignore */ }
}

async function registerPasskey() {
  passkeyRegistering.value = true
  passkeyError.value = ''
  try {
    const serverOptions = await passkeyHttp.addPath('register-options').post<any>()
    const publicKey: PublicKeyCredentialCreationOptions = {
      rp: serverOptions.rp,
      user: { ...serverOptions.user, id: base64UrlToBuffer(serverOptions.user.id) },
      challenge: base64UrlToBuffer(serverOptions.challenge),
      pubKeyCredParams: serverOptions.pubKeyCredParams,
      timeout: serverOptions.timeout,
      attestation: serverOptions.attestation,
      authenticatorSelection: serverOptions.authenticatorSelection,
      excludeCredentials: (serverOptions.excludeCredentials ?? []).map((c: any) => ({
        ...c, id: base64UrlToBuffer(c.id),
      })),
    }
    const credential = await navigator.credentials.create({ publicKey }) as PublicKeyCredential
    if (!credential) throw new Error('Credential creation cancelled')
    const response = credential.response as AuthenticatorAttestationResponse
    await passkeyHttp.addPath('register').post({
      id: credential.id, rawId: bufferToBase64Url(credential.rawId), type: credential.type,
      response: { attestationObject: bufferToBase64Url(response.attestationObject), clientDataJSON: bufferToBase64Url(response.clientDataJSON) },
    })
    await loadPasskeys()
  } catch (e: any) {
    if (e.name !== 'NotAllowedError')
      passkeyError.value = e.message || t('profile.passkeys.registerFailed', {}, 'Passkey registration failed.')
  } finally { passkeyRegistering.value = false }
}

async function deletePasskey(id: string) {
  const isLast = isLastMethodDisable('passkey')
  if (!confirm(isLast ? lastMethodWarning() : t('profile.passkeys.confirmRemove', {}, 'Really remove passkey?'))) return
  try {
    await passkeyHttp.addPath(id).delete()
    passkeys.value = passkeys.value.filter(p => p.Id !== id)
    if (isLast) await forceReauthAfterLastDisable()
  } catch { passkeyError.value = t('profile.passkeys.deleteFailed', {}, 'Deletion failed.') }
}

function base64UrlToBuffer(b: string): ArrayBuffer {
  const s = atob(b.replace(/-/g, '+').replace(/_/g, '/').padEnd(b.length + (4 - b.length % 4) % 4, '='))
  const a = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) a[i] = s.charCodeAt(i); return a.buffer
}
function bufferToBase64Url(b: ArrayBuffer): string {
  let s = ''; for (const x of new Uint8Array(b)) s += String.fromCharCode(x)
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function onMfaSetupClose(enabled: boolean) {
  showMfaSetup.value = false
  if (enabled) mfaStatus.value = { Enabled: true }
}
</script>

<template>
  <div class="flex min-h-0 flex-1">
    <!-- Left: Navigation menu -->
    <div class="sub-nav flex-shrink-0 p-2 flex flex-col min-h-0">
      <CoarMenu>
        <CoarMenuItem
          icon="user"
          :label="t('profile.tabAccount', {}, 'Account')"
          :class="{ 'profile-menu-item--active': activeSection === 'account' }"
          @clicked="activeSection = 'account'"
        />
        <CoarMenuItem
          icon="lock"
          :label="t('profile.tabSecurity', {}, 'Security')"
          :class="{ 'profile-menu-item--active': activeSection === 'security' }"
          @clicked="activeSection = 'security'"
        />
        <CoarMenuItem
          icon="monitor"
          :label="t('profile.tabSessions', {}, 'Sitzungen')"
          :class="{ 'profile-menu-item--active': activeSection === 'sessions' }"
          @clicked="activeSection = 'sessions'"
        />
        <CoarMenuItem
          icon="shield-check"
          :label="t('profile.tabPrivacy', {}, 'Datenschutz')"
          :class="{ 'profile-menu-item--active': activeSection === 'privacy' }"
          @clicked="activeSection = 'privacy'"
        />
        <CoarMenuItem
          icon="sliders-horizontal"
          :label="t('profile.tabPreferences', {}, 'Preferences')"
          :class="{ 'profile-menu-item--active': activeSection === 'preferences' }"
          @clicked="activeSection = 'preferences'"
        />
      </CoarMenu>
    </div>

    <!-- Right: Content -->
    <div class="flex-1 flex min-w-0 overflow-auto">
      <div class="w-11/12 mx-auto py-6 px-4 space-y-6">

        <!-- ═══ Account ═══ -->
        <template v-if="activeSection === 'account'">
          <CoarCard elevated>
            <div class="p-6 space-y-4">
              <h2 class="text-lg font-semibold">{{ t('profile.account', {}, 'Account') }}</h2>

              <!-- Readonly: UserName -->
              <div>
                <span class="text-surface-500 text-sm">{{ t('profile.username', {}, 'Username') }}</span>
                <div class="font-medium">{{ authStore.user?.UserName }}</div>
              </div>

              <!-- Editable form — one submit flows through the aggregate change-request.
                   Each field shows a pending-change hint inline when the user has an open
                   request for that field. -->
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <CoarFormField :label="t('profile.firstname', {}, 'Vorname')">
                    <CoarTextInput v-model="profileForm.Firstname" clearable />
                  </CoarFormField>
                  <div v-if="pendingFor('Firstname')" class="pending-hint">
                    <CoarIcon name="clock" size="s" />
                    <span>{{ t('profile.pending', { value: pendingFor('Firstname')?.NewValue || '—' }, 'Angefragt: {value}') }}</span>
                    <button type="button" class="pending-hint__revert" :title="t('profile.revert', {}, 'Reset')"
                      @click="revertPendingField('Firstname')">×</button>
                  </div>
                </div>
                <div>
                  <CoarFormField :label="t('profile.lastname', {}, 'Nachname')">
                    <CoarTextInput v-model="profileForm.Lastname" clearable />
                  </CoarFormField>
                  <div v-if="pendingFor('Lastname')" class="pending-hint">
                    <CoarIcon name="clock" size="s" />
                    <span>{{ t('profile.pending', { value: pendingFor('Lastname')?.NewValue || '—' }, 'Angefragt: {value}') }}</span>
                    <button type="button" class="pending-hint__revert" :title="t('profile.revert', {}, 'Reset')"
                      @click="revertPendingField('Lastname')">×</button>
                  </div>
                </div>
                <div>
                  <CoarFormField :label="t('profile.acronym', {}, 'Acronym')">
                    <CoarTextInput v-model="profileForm.Acronym" clearable />
                  </CoarFormField>
                  <div v-if="pendingFor('Acronym')" class="pending-hint">
                    <CoarIcon name="clock" size="s" />
                    <span>{{ t('profile.pending', { value: pendingFor('Acronym')?.NewValue || '—' }, 'Angefragt: {value}') }}</span>
                    <button type="button" class="pending-hint__revert" :title="t('profile.revert', {}, 'Reset')"
                      @click="revertPendingField('Acronym')">×</button>
                  </div>
                </div>
                <div>
                  <CoarFormField :label="t('profile.email.label', {}, 'E-Mail')">
                    <CoarTextInput v-model="profileForm.Email" type="email" clearable />
                  </CoarFormField>
                  <div v-if="pendingFor('Email')" class="pending-hint"
                    :class="{ 'pending-hint--verify': openRequest?.Status === 'EmailVerificationPending' }">
                    <CoarIcon :name="openRequest?.Status === 'EmailVerificationPending' ? 'mail' : 'clock'" size="s" />
                    <span>
                      <template v-if="openRequest?.Status === 'EmailVerificationPending'">
                        {{ t('profile.pendingEmailVerify', { email: pendingFor('Email')?.NewValue || '—' },
                            'Bestätigung an {email} ausstehend — bitte Posteingang prüfen') }}
                      </template>
                      <template v-else>
                        {{ t('profile.pending', { value: pendingFor('Email')?.NewValue || '—' }, 'Angefragt: {value}') }}
                      </template>
                    </span>
                    <button type="button" class="pending-hint__revert" :title="t('profile.revert', {}, 'Reset')"
                      @click="revertPendingField('Email')">×</button>
                  </div>
                </div>
              </div>

              <CoarNote v-if="emailUnverified" variant="warning">
                {{ t('profile.lockedUnverified', {}, 'Profile changes are blocked until you verify your email address.') }}
              </CoarNote>

              <div class="flex items-center gap-3 flex-wrap">
                <CoarButton :disabled="!profileDirty || emailUnverified" :loading="profileSaving" @click="saveProfile">
                  {{ t('common.save', {}, 'Speichern') }}
                </CoarButton>
                <span v-if="profileSavedHint" class="text-sm text-green-700">
                  <CoarIcon name="check" size="s" class="inline-block" /> {{ t('profile.saved', {}, 'Gespeichert') }}
                </span>
                <span v-if="profileError" class="text-sm text-red-600">{{ profileError }}</span>

                <button v-if="openRequest" type="button"
                  class="ml-auto text-sm text-surface-500 hover:text-red-600 underline"
                  @click="cancelRequest">
                  {{ t('profile.request.withdrawAll', {}, 'Withdraw request') }}
                </button>
              </div>

              <p class="text-xs text-surface-500">
                {{ t('profile.changeHint', {}, 'Profile changes are reviewed by an administrator before being applied. Email changes also require you to confirm the new address via a link sent to your inbox.') }}
              </p>
            </div>
          </CoarCard>

          <!-- Last rejected request — surface the reviewer note so the user knows why -->
          <CoarCard v-if="!openRequest && lastTerminal?.Status === 'Rejected' && lastTerminal.ReviewerNote" elevated>
            <div class="p-6 space-y-2">
              <CoarNote variant="warning">
                {{ t('profile.request.lastRejected', {}, 'Your last change request was rejected.') }}
              </CoarNote>
              <p class="text-sm">
                <span class="text-surface-500">{{ t('profile.email.rejectedNote', {}, 'Reason:') }}</span>
                <span class="ml-1">{{ lastTerminal.ReviewerNote }}</span>
              </p>
            </div>
          </CoarCard>

          <CoarCard elevated>
            <div class="p-6">
              <h2 class="text-lg font-semibold mb-4">{{ t('profile.permissions', {}, 'Permissions') }}</h2>
              <div class="flex flex-wrap gap-2">
                <span v-for="perm in authStore.permissions" :key="perm"
                  class="rounded-full bg-surface-100 px-3 py-1 text-xs font-medium text-surface-700"
                >{{ perm }}</span>
                <span v-if="!authStore.permissions.length" class="text-sm text-surface-400">
                  {{ t('profile.noPermissions', {}, 'No permissions assigned.') }}
                </span>
              </div>
            </div>
          </CoarCard>
        </template>

        <!-- ═══ Security ═══ -->
        <template v-if="activeSection === 'security'">
        <div class="profile-card-grid">
          <!-- Change Password -->
          <CoarCard elevated>
            <div class="p-6">
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-3">
                  <CoarIcon name="key" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.changePassword.title', {}, 'Change Password') }}</h2>
                </div>
                <CoarButton @click="showChangePassword = true">
                  {{ t('profile.changePassword.button', {}, 'Change Password') }}
                </CoarButton>
              </div>
            </div>
          </CoarCard>

          <!-- MFA -->
          <CoarCard elevated>
            <div class="p-6">
              <div class="flex items-center justify-between mb-4">
                <div class="flex items-center gap-3">
                  <CoarIcon name="smartphone" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.mfa.title', {}, 'Two-Factor Authentication') }}</h2>
                </div>
                <span v-if="mfaStatus" class="rounded-full px-3 py-1 text-xs font-medium"
                  :class="mfaStatus.Enabled ? 'bg-green-100 text-green-800' : 'bg-surface-100 text-surface-600'"
                >{{ mfaStatus.Enabled ? t('common.enabled', {}, 'Enabled') : t('common.disabled', {}, 'Disabled') }}</span>
              </div>
              <template v-if="mfaStatus && !mfaStatus.Enabled">
                <p class="text-sm text-surface-600 mb-4">{{ t('profile.mfa.setupDescription', {}, 'Protect your account with an authenticator app.') }}</p>
                <CoarButton @click="showMfaSetup = true">{{ t('profile.mfa.setupButton', {}, 'Set up MFA') }}</CoarButton>
              </template>
              <template v-else-if="mfaStatus?.Enabled">
                <p class="text-sm text-surface-600 mb-4">{{ t('profile.mfa.enabledDescription', {}, 'Your account is protected by an authenticator app.') }}</p>
                <CoarButton variant="danger" :loading="disabling" @click="disableMfa">{{ t('profile.mfa.disableButton', {}, 'Disable MFA') }}</CoarButton>
              </template>
            </div>
          </CoarCard>

          <!-- Email OTP -->
          <CoarCard elevated>
            <div class="p-6">
              <div class="flex items-center justify-between mb-4">
                <div class="flex items-center gap-3">
                  <CoarIcon name="mail" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.emailOtp.title', {}, 'Email Code (OTP)') }}</h2>
                </div>
                <span v-if="emailOtpStatus" class="rounded-full px-3 py-1 text-xs font-medium"
                  :class="emailOtpStatus.Enabled ? 'bg-green-100 text-green-800' : 'bg-surface-100 text-surface-600'"
                >{{ emailOtpStatus.Enabled ? t('common.enabled', {}, 'Enabled') : t('common.disabled', {}, 'Disabled') }}</span>
              </div>
              <template v-if="emailOtpStatus && !emailOtpStatus.HasEmail">
                <p class="text-sm text-surface-600">{{ t('profile.emailOtp.noEmail', {}, 'An email address is required.') }}</p>
              </template>
              <template v-else-if="emailOtpStatus && !emailOtpStatus.Enabled">
                <p class="text-sm text-surface-600 mb-4">{{ t('profile.emailOtp.description', {}, 'A one-time code will be sent to your email.') }}</p>
                <!-- Enabling Email-OTP makes the inbox load-bearing; gate it
                     on a verified email. Disable is left ungated so users
                     who accidentally turned it on can recover. -->
                <CoarNote v-if="emailUnverified" variant="warning" class="mb-2">
                  {{ t('profile.emailOtp.lockedUnverified', {}, 'Enabling Email-OTP is blocked until you verify your email address.') }}
                </CoarNote>
                <CoarButton :loading="emailOtpToggling" :disabled="emailUnverified" @click="toggleEmailOtp">
                  {{ t('profile.emailOtp.enableButton', {}, 'Enable email code') }}
                </CoarButton>
              </template>
              <template v-else-if="emailOtpStatus?.Enabled">
                <p class="text-sm text-surface-600 mb-4">{{ t('profile.emailOtp.enabledDescription', {}, 'A one-time code will be sent to') }} <strong>{{ authStore.user?.Email }}</strong></p>
                <CoarButton variant="danger" :loading="emailOtpToggling" @click="toggleEmailOtp">{{ t('profile.emailOtp.disableButton', {}, 'Disable email code') }}</CoarButton>
              </template>
            </div>
          </CoarCard>

          <!-- Passkeys -->
          <CoarCard elevated>
            <div class="p-6">
              <div class="flex items-center justify-between mb-4">
                <div class="flex items-center gap-3">
                  <CoarIcon name="fingerprint" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.passkeys.title', {}, 'Passkeys') }}</h2>
                </div>
                <span class="rounded-full px-3 py-1 text-xs font-medium"
                  :class="passkeys.length > 0 ? 'bg-green-100 text-green-800' : 'bg-surface-100 text-surface-600'"
                >{{ passkeys.length > 0 ? t('profile.passkeys.registered', { count: passkeys.length }, '{count} registered') : t('profile.passkeys.none', {}, 'None') }}</span>
              </div>
              <p class="text-sm text-surface-600 mb-4">{{ t('profile.passkeys.description', {}, 'Sign in without a password.') }}</p>
              <div v-if="passkeys.length" class="space-y-2 mb-4">
                <div v-for="pk in passkeys" :key="pk.Id" class="flex items-center justify-between rounded border border-surface-200 bg-surface-50 px-4 py-3">
                  <div class="flex items-center gap-3">
                    <CoarIcon name="fingerprint" size="m" class="text-surface-500" />
                    <div>
                      <div class="text-sm font-medium">{{ pk.DisplayName }}</div>
                      <div class="text-xs text-surface-400">
                        {{ t('profile.passkeys.created', {}, 'Created: ') }}{{ new Date(pk.CreatedAt).toLocaleDateString() }}
                        <template v-if="pk.LastUsedAt"> · {{ t('profile.passkeys.lastUsed', {}, 'Last used: ') }}{{ new Date(pk.LastUsedAt).toLocaleDateString() }}</template>
                      </div>
                    </div>
                  </div>
                  <button class="text-surface-400 hover:text-red-600 transition" @click="deletePasskey(pk.Id)">
                    <CoarIcon name="trash-2" size="s" />
                  </button>
                </div>
              </div>
              <CoarButton :loading="passkeyRegistering" @click="registerPasskey">
                <CoarIcon name="plus" size="s" class="mr-1" /> {{ t('profile.passkeys.registerButton', {}, 'Register Passkey') }}
              </CoarButton>
              <p v-if="passkeyError" class="mt-2 text-sm text-red-600">{{ passkeyError }}</p>
            </div>
          </CoarCard>

          <!-- External identity links (IdP accounts bound to this user) -->
          <CoarCard elevated v-if="externalLinks.length > 0 || availableIdps.length > 0">
            <div class="p-6">
              <div class="flex items-center justify-between mb-4">
                <div class="flex items-center gap-3">
                  <CoarIcon name="key-round" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.externalLinks.title', {}, 'Linked accounts') }}</h2>
                </div>
              </div>
              <p class="text-sm text-surface-600 mb-4">
                {{ t('profile.externalLinks.description', {}, 'Sign in to Cocoar.Auth using these identity providers.') }}
              </p>

              <!-- Existing links — click the row to expand the last known claim snapshot -->
              <div v-if="externalLinks.length > 0" class="mb-4 space-y-2">
                <div v-for="link in externalLinks" :key="link.Id"
                     class="rounded border border-surface-200 overflow-hidden">
                  <div class="flex items-center justify-between py-2 px-3">
                    <button class="flex items-center gap-2 min-w-0 flex-1 text-left hover:bg-surface-50 -mx-3 -my-2 px-3 py-2 transition"
                            type="button"
                            @click="toggleLinkExpand(link.Id)">
                      <CoarIcon :name="expandedLinks.has(link.Id) ? 'chevron-down' : 'chevron-right'" size="s" />
                      <div class="min-w-0 flex-1">
                        <div class="text-sm font-medium">{{ link.ProviderDisplayName }}</div>
                        <div class="text-xs text-surface-500 truncate">
                          {{ link.Email ?? link.Issuer }}
                          <span v-if="link.LastLoginAt"> · {{ t('profile.externalLinks.lastLogin', {}, 'Last login: ') }}{{ new Date(link.LastLoginAt).toLocaleDateString() }}</span>
                        </div>
                      </div>
                    </button>
                    <button class="text-surface-400 hover:text-red-600 transition ml-2" @click="unlink(link.Id, link.ProviderDisplayName)">
                      <CoarIcon name="trash-2" size="s" />
                    </button>
                  </div>
                  <div v-if="expandedLinks.has(link.Id)" class="border-t border-surface-100 bg-surface-50 px-3 py-2 text-xs">
                    <dl class="link-detail">
                      <dt>{{ t('profile.externalLinks.issuer', {}, 'Issuer') }}</dt>
                      <dd><code>{{ link.Issuer }}</code></dd>
                      <dt v-if="link.Email">{{ t('profile.externalLinks.email', {}, 'Email') }}</dt>
                      <dd v-if="link.Email">{{ link.Email }}</dd>
                      <dt>{{ t('profile.externalLinks.linkedAt', {}, 'Linked') }}</dt>
                      <dd>{{ new Date(link.LinkedAt).toLocaleString() }}</dd>
                      <dt v-if="link.LastCapturedAt">{{ t('profile.externalLinks.capturedAt', {}, 'Last captured') }}</dt>
                      <dd v-if="link.LastCapturedAt">{{ new Date(link.LastCapturedAt).toLocaleString() }}</dd>
                    </dl>
                    <div v-if="!link.LastScriptSucceeded" class="text-red-600 mt-1">
                      · {{ t('profile.externalLinks.scriptFailed', {}, 'User-update script failed at last login') }}
                      <span v-if="link.LastScriptError" class="text-surface-500"> ({{ link.LastScriptError }})</span>
                    </div>
                  </div>
                </div>
              </div>

              <!-- IdPs the user hasn't linked yet -->
              <div v-if="unlinkedIdps.length > 0" class="flex flex-wrap gap-2">
                <CoarButton v-for="idp in unlinkedIdps" :key="idp.Id"
                            variant="secondary"
                            @click="linkWith(idp.Id)">
                  <CoarIcon v-if="idp.IconName" :name="idp.IconName" size="s" class="mr-1" />
                  {{ t('profile.externalLinks.linkPrefix', {}, 'Link with') }} {{ idp.DisplayName }}
                </CoarButton>
              </div>

              <p v-if="linksError" class="mt-2 text-sm text-red-600">{{ linksError }}</p>
            </div>
          </CoarCard>
        </div>
        </template>

        <!-- ═══ Sessions ═══ -->
        <template v-if="activeSection === 'sessions'">
          <CoarCard elevated>
            <div class="p-6">
              <div class="flex items-center justify-between mb-4">
                <div class="flex items-center gap-3">
                  <CoarIcon name="monitor" size="m" class="text-surface-500" />
                  <div>
                    <h2 class="text-lg font-semibold">{{ t('profile.sessions.title', {}, 'Aktive Sitzungen') }}</h2>
                    <p class="text-sm text-surface-500">{{ t('profile.sessions.description', {}, 'Geräte, mit denen du derzeit angemeldet bist.') }}</p>
                  </div>
                </div>
                <CoarButton variant="danger" :loading="revokingAll"
                  :disabled="sessionsLoading || sessions.length === 0"
                  @click="revokeAllSessions">
                  {{ t('profile.sessions.revokeAll', {}, 'Überall abmelden') }}
                </CoarButton>
              </div>

              <div v-if="sessionsLoading && sessions.length === 0" class="text-sm text-surface-400">
                {{ t('common.loading', {}, 'Laden...') }}
              </div>
              <div v-else-if="sessionsError" class="text-sm text-red-600">{{ sessionsError }}</div>
              <div v-else-if="sessions.length === 0" class="text-sm text-surface-400">
                {{ t('profile.sessions.none', {}, 'Keine Sitzungen vorhanden.') }}
              </div>
              <div v-else class="space-y-2">
                <div v-for="s in sessions" :key="s.Id"
                  class="flex items-center gap-3 rounded border border-surface-200 bg-surface-50 px-4 py-3"
                  :class="{ 'session-current': s.IsCurrent }">
                  <CoarIcon :name="deviceIcon(s)" size="m" class="text-surface-500" />
                  <div class="flex-1 min-w-0">
                    <div class="text-sm font-medium flex items-center gap-2">
                      {{ deviceLabel(s) }}
                      <span v-if="s.IsCurrent" class="rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-800">
                        {{ t('profile.sessions.current', {}, 'Aktuelle Sitzung') }}
                      </span>
                    </div>
                    <div class="text-xs text-surface-500 truncate">
                      <span v-if="s.IpAddress">IP: {{ s.IpAddress }} · </span>
                      {{ t('profile.sessions.lastActive', {}, 'Zuletzt aktiv:') }}
                      {{ new Date(s.LastActiveAt).toLocaleString() }}
                      · {{ t('profile.sessions.created', {}, 'Erstellt:') }}
                      {{ new Date(s.CreatedAt).toLocaleDateString() }}
                    </div>
                  </div>
                  <button class="text-surface-400 hover:text-red-600 transition"
                    :disabled="s.IsCurrent || revokingSessionId === s.Id"
                    :title="s.IsCurrent
                      ? t('profile.sessions.cantRevokeCurrent', {}, 'Aktuelle Sitzung kann hier nicht beendet werden — bitte abmelden.')
                      : t('profile.sessions.revoke', {}, 'Beenden')"
                    @click="revokeSession(s.Id)">
                    <CoarIcon name="log-out" size="s" />
                  </button>
                </div>
              </div>
            </div>
          </CoarCard>
        </template>

        <!-- ═══ Privacy / GDPR ═══ -->
        <template v-if="activeSection === 'privacy'">
          <div class="profile-card-grid">
            <!-- Data export -->
            <CoarCard elevated>
              <div class="p-6 space-y-3">
                <div class="flex items-center gap-3">
                  <CoarIcon name="download" size="m" class="text-surface-500" />
                  <h2 class="text-lg font-semibold">{{ t('profile.privacy.exportTitle', {}, 'Daten exportieren') }}</h2>
                </div>
                <p class="text-sm text-surface-600">
                  {{ t('profile.privacy.exportDescription', {},
                    'Alle Daten, die wir über dich gespeichert haben (Profil, Sicherheit, Sitzungen, Login-Historie), als JSON-Download.') }}
                </p>
                <CoarButton :loading="exportRunning" @click="exportMyData">
                  {{ t('profile.privacy.exportButton', {}, 'Export herunterladen') }}
                </CoarButton>
              </div>
            </CoarCard>

            <!-- Account deletion -->
            <CoarCard elevated>
              <div class="p-6 space-y-3">
                <div class="flex items-center gap-3">
                  <CoarIcon name="user-x" size="m" class="text-red-600" />
                  <h2 class="text-lg font-semibold">{{ t('profile.privacy.deleteTitle', {}, 'Konto löschen') }}</h2>
                </div>

                <CoarNote v-if="deletionStatus?.IsPending" variant="warning">
                  {{ t('profile.privacy.statusPending', {}, 'Löschanfrage läuft.') }}
                  <span v-if="deletionStatus.ConfirmationDeadline">
                    {{ t('profile.privacy.confirmBy', {}, 'Bitte bis') }}
                    {{ new Date(deletionStatus.ConfirmationDeadline).toLocaleString() }}
                    {{ t('profile.privacy.confirmEmail', {}, 'über die zugesandte Mail bestätigen.') }}
                  </span>
                </CoarNote>
                <CoarNote v-else-if="deletionStatus?.IsDataMasked" variant="info">
                  {{ t('profile.privacy.statusMasked', {}, 'Personenbezogene Daten wurden bereits maskiert.') }}
                </CoarNote>
                <p v-else class="text-sm text-surface-600">
                  {{ t('profile.privacy.deleteDescription', {},
                    'Persönliche Daten werden nach Bestätigung per E-Mail-Link gelöscht. Aus Audit-Gründen bleibt der Event-Stream maskiert erhalten.') }}
                </p>

                <div v-if="deletionStatus?.IsPending" class="flex gap-2">
                  <CoarButton variant="secondary" :loading="deleteCancelRunning" @click="cancelDeletion">
                    {{ t('profile.privacy.cancelButton', {}, 'Anfrage zurückziehen') }}
                  </CoarButton>
                </div>
                <div v-else-if="!deletionStatus?.IsDeleted">
                  <div v-if="!showDeleteForm">
                    <CoarButton variant="danger" @click="showDeleteForm = true">
                      {{ t('profile.privacy.deleteButton', {}, 'Konto löschen anfragen') }}
                    </CoarButton>
                  </div>
                  <div v-else class="space-y-2">
                    <CoarFormField :label="t('profile.privacy.password', {}, 'Aktuelles Passwort')">
                      <CoarTextInput v-model="deletePassword" type="password" />
                    </CoarFormField>
                    <CoarFormField :label="t('profile.privacy.reason', {}, 'Grund (optional)')">
                      <CoarTextInput v-model="deleteReason" />
                    </CoarFormField>
                    <div class="flex gap-2">
                      <CoarButton variant="danger" :loading="deleteRequestRunning" @click="requestDeletion">
                        {{ t('profile.privacy.confirmDelete', {}, 'Bestätigungs-Mail senden') }}
                      </CoarButton>
                      <CoarButton variant="ghost" @click="showDeleteForm = false; deletePassword = ''; deleteReason = ''">
                        {{ t('common.cancel', {}, 'Abbrechen') }}
                      </CoarButton>
                    </div>
                  </div>
                </div>

                <p v-if="privacyError" class="text-sm text-red-600">{{ privacyError }}</p>
                <p v-if="privacyMessage" class="text-sm text-green-700">{{ privacyMessage }}</p>
              </div>
            </CoarCard>
          </div>
        </template>

        <!-- ═══ Preferences ═══ -->
        <template v-if="activeSection === 'preferences'">
        <div class="profile-card-grid">
          <CoarCard elevated>
            <div class="p-6">
              <h2 class="text-lg font-semibold mb-2">{{ t('profile.preferences.language', {}, 'Language & Region') }}</h2>
              <p class="text-sm text-surface-600 mb-4">{{ t('profile.preferences.languageDescription', {}, 'Choose the display language and regional formatting (dates, numbers).') }}</p>
              <CoarSelect :model-value="language" :options="localeSelectOptions" @update:model-value="onLocaleChange" class="max-w-xs" />
            </div>
          </CoarCard>

          <CoarCard elevated>
            <div class="p-6">
              <h2 class="text-lg font-semibold mb-2">{{ t('profile.preferences.theme', {}, 'Appearance') }}</h2>
              <p class="text-sm text-surface-600 mb-4">{{ t('profile.preferences.themeDescription', {}, 'Choose between light and dark design.') }}</p>
              <CoarSelect v-model="themeValue" :options="[
                { value: 'light', label: t('profile.preferences.lightMode', {}, 'Light') },
                { value: 'dark', label: t('profile.preferences.darkMode', {}, 'Dark') },
              ]" class="max-w-xs" />
            </div>
          </CoarCard>
        </div>
        </template>
      </div>
    </div>
  </div>

  <!-- MFA Setup Modal -->
  <Teleport to="body">
    <div v-if="showMfaSetup" class="fixed inset-0 z-[1000] flex items-center justify-center bg-black/40" @click.self="onMfaSetupClose(false)">
      <MfaSetupModal :close="onMfaSetupClose" />
    </div>
  </Teleport>

  <Teleport to="body">
    <div v-if="showChangePassword" class="fixed inset-0 z-[1000] flex items-center justify-center bg-black/40" @click.self="showChangePassword = false">
      <ChangePasswordModal :close="() => showChangePassword = false" />
    </div>
  </Teleport>
</template>

<style scoped>
.link-detail {
  display: grid;
  grid-template-columns: 7rem 1fr;
  gap: 2px 8px;
  margin: 0;
  font-size: 0.72rem;
}
.link-detail dt {
  font-weight: 500;
  color: #525e76;
  text-transform: uppercase;
  letter-spacing: 0.02em;
  font-size: 0.7rem;
  padding-top: 2px;
}
.link-detail dd {
  margin: 0;
}
.link-detail code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  background: #e5e7eb;
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 0.7rem;
  word-break: break-all;
}
.pending-hint {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-top: 2px;
  padding: 2px 8px;
  border-radius: 4px;
  background-color: #fef3c7;
  color: #92400e;
  font-size: 0.75rem;
  font-weight: 500;
}
.pending-hint--verify {
  background-color: #dbeafe;
  color: #1e40af;
}
.pending-hint__revert {
  margin-left: 4px;
  padding: 0 4px;
  border: 0;
  background: transparent;
  color: inherit;
  cursor: pointer;
  opacity: 0.6;
  font-size: 1em;
  line-height: 1;
}
.pending-hint__revert:hover { opacity: 1; }

.sub-nav {
  width: 13rem;
  height: 100%;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
}

.profile-card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 1.5rem;
}

.session-current {
  border-color: var(--coar-border-semantic-success, #86efac) !important;
  background: var(--coar-background-semantic-success-subtle, #f0fdf4) !important;
}

.profile-menu-item--active {
  background: var(--coar-menu-item-background-active, #eff6ff);
  color: var(--coar-menu-item-text-active, #1d4ed8);
  font-weight: 500;
  border-radius: 6px;
}
</style>
