<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarIcon, CoarSpinner, CoarNote, CoarTag } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useHttpClient } from '@/composables/useHttpClient'
import { useAuthStore } from '@/stores/auth.store'
import { useIsAdmin } from '@/composables/useIsAdmin'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { SessionDto, SessionListDto } from '@/models/session'
import type { UserDto } from '@/models/user'

const { t, language } = useI18n()
const router = useRouter()
const authStore = useAuthStore()
const isAdmin = useIsAdmin()
const loginProviderStore = useLoginProviderStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('dashboard.title', {}, 'Dashboard')
  ctx.header.icon = 'layout-dashboard'
  ctx.content.container = true
}), { immediate: true })

// ─── Account-Sicherheit ──────────────────────────────────────────────────
// We need passkey count from /api/account/passkey to know whether a passkey
// is registered — Has2FA + TwoFactorMethods on AuthUser tell us about TOTP /
// email / passkey-as-2FA, but the dashboard checklist treats passkeys as a
// separate "passwordless" capability and surfaces them independently.
interface PasskeyDto { Id: string; DisplayName: string; CreatedAt: string; LastUsedAt: string | null }
const passkeysHttp = useHttpClient('/api/account/passkey')
const passkeyCount = ref<number | null>(null)
const passkeysError = ref(false)

async function loadPasskeys() {
  try {
    const list = await passkeysHttp.get<PasskeyDto[]>()
    passkeyCount.value = list.length
  } catch {
    passkeysError.value = true
    passkeyCount.value = 0
  }
}

interface SecurityItem {
  /** Stable key — used as Vue list key + i18n suffix. */
  key: 'email' | 'mfa' | 'passkey'
  ok: boolean
  label: string
  icon: 'circle-check' | 'shield-alert' | 'circle-off'
  iconClass: string
  hint: string | null
}

const securityItems = computed<SecurityItem[]>(() => {
  const items: SecurityItem[] = []

  const hasEmail = !!authStore.user?.Email
  items.push({
    key: 'email',
    ok: hasEmail,
    label: t('dashboard.security.emailLabel', {}, 'E-Mail hinterlegt'),
    icon: hasEmail ? 'circle-check' : 'circle-off',
    iconClass: hasEmail ? 'text-green-600' : 'text-surface-400',
    hint: hasEmail ? null : t('dashboard.security.emailHint', {}, 'Jetzt einrichten'),
  })

  const has2fa = authStore.user?.Has2FA === true
  items.push({
    key: 'mfa',
    ok: has2fa,
    label: t('dashboard.security.mfaLabel', {}, 'Zwei-Faktor-Authentisierung'),
    icon: has2fa ? 'circle-check' : 'shield-alert',
    iconClass: has2fa ? 'text-green-600' : 'text-amber-600',
    hint: has2fa ? null : t('dashboard.security.mfaHint', {}, 'Jetzt einrichten'),
  })

  const hasPasskey = (passkeyCount.value ?? 0) > 0
  items.push({
    key: 'passkey',
    ok: hasPasskey,
    // While the count is still unknown (null) we render the row neutrally —
    // CoarSpinner replaces the icon (see template) so we don't briefly flash
    // a red "missing" state on a perfectly fine account.
    label: t('dashboard.security.passkeyLabel', {}, 'Passkey hinterlegt'),
    icon: hasPasskey ? 'circle-check' : 'circle-off',
    iconClass: hasPasskey ? 'text-green-600' : 'text-surface-400',
    hint: hasPasskey ? null : t('dashboard.security.passkeyHint', {}, 'Jetzt einrichten'),
  })

  return items
})

const securityNeedsAttention = computed(() => securityItems.value.some(i => !i.ok))

function goToProfileSecurity() {
  router.push('/profile')
}

// ─── Aktive Sessions ──────────────────────────────────────────────────────
const sessionsHttp = useHttpClient('/api/auth/sessions')
const sessions = ref<SessionDto[]>([])
const sessionsLoading = ref(true)
const sessionsError = ref(false)

async function loadSessions() {
  try {
    const res = await sessionsHttp.get<SessionListDto>()
    sessions.value = res.Sessions ?? []
  } catch {
    sessionsError.value = true
  } finally {
    sessionsLoading.value = false
  }
}

const topSessions = computed(() => sessions.value.slice(0, 3))
const extraSessionCount = computed(() => Math.max(0, sessions.value.length - 3))

function deviceIcon(s: SessionDto): string {
  const dt = (s.DeviceType ?? '').toLowerCase()
  if (dt.includes('mobile') || dt.includes('phone')) return 'smartphone'
  if (dt.includes('tablet')) return 'tablet'
  return 'monitor'
}

function deviceLabel(s: SessionDto): string {
  const browser = [s.Browser, s.BrowserVersion].filter(Boolean).join(' ')
  const os = [s.OperatingSystem, s.OsVersion].filter(Boolean).join(' ')
  return [browser, os].filter(Boolean).join(' · ') ||
    (s.DeviceType ?? t('dashboard.sessions.unknownDevice', {}, 'Unbekanntes Gerät'))
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime()
  const diffSec = Math.max(0, Math.floor((Date.now() - then) / 1000))
  if (diffSec < 60) return t('dashboard.time.justNow', {}, 'gerade eben')
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return t('dashboard.time.minutesAgo', { n: diffMin }, 'vor {n} Min.')
  const diffH = Math.floor(diffMin / 60)
  if (diffH < 24) return t('dashboard.time.hoursAgo', { n: diffH }, 'vor {n} Std.')
  const diffD = Math.floor(diffH / 24)
  if (diffD < 30) return t('dashboard.time.daysAgo', { n: diffD }, 'vor {n} Tagen')
  return new Date(iso).toLocaleDateString()
}

function goToProfileSessions() {
  router.push('/profile')
}

// ─── Admin: Realm-Übersicht ──────────────────────────────────────────────
// We grab the user list (admin-gated) for the count, and re-use the AuthLog
// fetch (also admin-gated) below to derive failed-logins-last-24h.
const usersHttp = useHttpClient('/api/user')
const userCount = ref<number | null>(null)
const userCountError = ref(false)

async function loadUserCount() {
  try {
    const res = await usersHttp.get<UserDto[]>()
    userCount.value = res.length
  } catch {
    userCountError.value = true
  }
}

// ─── Admin: Auth log (drives both System-Aktivität + failed-24h counter) ──
interface AuthLogEntry {
  Timestamp: string
  Level: string
  Message: string
  UserName: string | null
  Ip: string | null
}
const authLogHttp = useHttpClient('/api/admin/auth-log')
const authLog = ref<AuthLogEntry[]>([])
const authLogLoading = ref(true)
const authLogError = ref(false)

async function loadAuthLog() {
  try {
    // Pull a window large enough to count failures across 24h on a busy realm
    // without paging — the endpoint defaults to 200, we ask for 500 to give
    // the failed-24h counter some headroom. The "System activity" card only
    // displays the first 10 of these.
    authLog.value = await authLogHttp.setQueryParameter('limit', '500').get<AuthLogEntry[]>()
  } catch {
    authLogError.value = true
  } finally {
    authLogLoading.value = false
  }
}

const failedLogins24h = computed(() => {
  if (authLogError.value) return null
  const cutoff = Date.now() - 24 * 60 * 60 * 1000
  return authLog.value.filter(e =>
    (e.Level === 'Warning' || e.Level === 'Error') &&
    new Date(e.Timestamp).getTime() >= cutoff,
  ).length
})

const recentSystemActivity = computed(() => authLog.value.slice(0, 10))

function authLogLevelVariant(level: string): 'neutral' | 'warning' | 'error' {
  if (level === 'Error') return 'error'
  if (level === 'Warning') return 'warning'
  return 'neutral'
}

function goToAuthLog() {
  router.push('/admin/auth-log')
}

function goToUsers() {
  router.push('/admin/users')
}

// ─── Admin: Pending Change Requests ──────────────────────────────────────
interface ChangeRequestRow {
  Id: string
  UserId: string
  UserLabel: string
  Type: string
  Status: 'EmailVerificationPending' | 'AdminApprovalPending' | 'Approved' | 'Rejected'
  RequestedAt: string
  UpdatedAt: string
}
const changeRequestsHttp = useHttpClient('/api/admin/change-requests')
const changeRequests = ref<ChangeRequestRow[]>([])
const changeRequestsLoading = ref(true)
const changeRequestsError = ref(false)

async function loadChangeRequests() {
  try {
    // Default `includeTerminal=false` already filters out Approved/Rejected,
    // so the list IS the pending count.
    changeRequests.value = await changeRequestsHttp.get<ChangeRequestRow[]>()
  } catch {
    changeRequestsError.value = true
  } finally {
    changeRequestsLoading.value = false
  }
}

const topPendingRequests = computed(() => changeRequests.value.slice(0, 5))

function changeRequestTypeLabel(type: string): string {
  if (type === 'Profile') return t('dashboard.pendingChangeRequests.typeProfile', {}, 'Profiländerung')
  return type
}

function changeRequestStatusLabel(status: ChangeRequestRow['Status']): string {
  if (status === 'EmailVerificationPending')
    return t('dashboard.pendingChangeRequests.statusVerify', {}, 'E-Mail-Bestätigung offen')
  if (status === 'AdminApprovalPending')
    return t('dashboard.pendingChangeRequests.statusAdmin', {}, 'Wartet auf Freigabe')
  return status
}

function goToChangeRequests() {
  router.push('/admin/change-requests')
}

// ─── Admin: Login-Provider-Status ─────────────────────────────────────────
const providersLoading = ref(true)
const providersError = ref(false)

async function loadLoginProviders() {
  try {
    await loginProviderStore.loadAll()
  } catch {
    providersError.value = true
  } finally {
    providersLoading.value = false
  }
}

const loginProviders = computed(() => loginProviderStore.providers)

function goToLoginProvider(id: string) {
  // Provider detail is a URL-fragment-routed modal mounted by LoginProviderList
  // (`useRoutedModals()` reads the hash). Navigate to the list with the id in
  // the hash so the modal pops on arrival, matching the in-page click behavior.
  router.push({ path: '/admin/login-providers', hash: `#${id}` })
}

function goToLoginProviders() {
  router.push('/admin/login-providers')
}

// ─── Bootstrap ────────────────────────────────────────────────────────────
onMounted(() => {
  // Personal cards — always.
  loadPasskeys()
  loadSessions()

  // Admin cards — only fire the (gated) endpoints if we already know the user
  // has admin powers. Hitting them as a normal user would just earn 403s and
  // surface "Daten konnten nicht geladen werden" cards on a perfectly fine
  // dashboard.
  if (isAdmin.value) {
    loadUserCount()
    loadAuthLog()
    loadChangeRequests()
    loadLoginProviders()
  }
})
</script>

<template>
  <div class="w-full py-6 space-y-6">
    <!-- Personal cards: 3-col on lg+, 2-col on md, 1-col mobile.
         Keeps the security checklist + sessions side by side on a normal
         desktop, falls back gracefully on narrower viewports. -->
    <div class="dashboard-grid">
      <!-- ─── Account-Sicherheit ─── -->
      <CoarCard elevated>
        <div class="p-6 space-y-4">
          <div class="flex items-center gap-3">
            <CoarIcon name="shield-check" size="m" class="text-surface-500" />
            <h2 class="text-lg font-semibold">
              {{ t('dashboard.security.title', {}, 'Account-Sicherheit') }}
            </h2>
          </div>

          <ul class="space-y-2">
            <li v-for="item in securityItems" :key="item.key" class="flex items-center gap-3 text-sm">
              <!-- Spinner placeholder while we're still resolving the passkey
                   list — prevents a "Passkey fehlt" flash on accounts that
                   actually have one. -->
              <CoarSpinner v-if="item.key === 'passkey' && passkeyCount === null" size="s" />
              <CoarIcon v-else :name="item.icon" size="s" :class="item.iconClass" />
              <span class="flex-1">{{ item.label }}</span>
              <button
                v-if="item.hint"
                type="button"
                class="text-xs text-blue-600 hover:underline"
                @click="goToProfileSecurity"
              >
                {{ item.hint }}
              </button>
            </li>
          </ul>

          <div v-if="securityNeedsAttention" class="pt-2">
            <button
              type="button"
              class="text-sm text-blue-600 hover:underline"
              @click="goToProfileSecurity"
            >
              {{ t('dashboard.security.cta', {}, 'Zum Profil →') }}
            </button>
          </div>
        </div>
      </CoarCard>

      <!-- ─── Aktive Sessions ─── -->
      <CoarCard elevated>
        <div class="p-6 space-y-4">
          <div class="flex items-center gap-3">
            <CoarIcon name="monitor" size="m" class="text-surface-500" />
            <h2 class="text-lg font-semibold">
              {{ t('dashboard.sessions.title', {}, 'Aktive Sitzungen') }}
            </h2>
          </div>

          <div v-if="sessionsLoading" class="flex justify-center py-4">
            <CoarSpinner size="m" />
          </div>
          <CoarNote v-else-if="sessionsError" variant="error">
            {{ t('dashboard.errors.loadFailed', {}, 'Daten konnten nicht geladen werden.') }}
          </CoarNote>
          <div v-else-if="sessions.length === 0" class="text-sm text-surface-400">
            {{ t('dashboard.sessions.none', {}, 'Keine Sitzungen vorhanden.') }}
          </div>
          <div v-else class="space-y-2">
            <div
              v-for="s in topSessions"
              :key="s.Id"
              class="flex items-center gap-3 rounded border border-surface-200 bg-surface-50 px-3 py-2"
              :class="{ 'session-current': s.IsCurrent }"
            >
              <CoarIcon :name="deviceIcon(s)" size="s" class="text-surface-500" />
              <div class="flex-1 min-w-0">
                <div class="text-sm font-medium flex items-center gap-2 truncate">
                  <span class="truncate">{{ deviceLabel(s) }}</span>
                  <CoarTag v-if="s.IsCurrent" variant="success" size="s">
                    {{ t('dashboard.sessions.thisDevice', {}, 'Dieses Gerät') }}
                  </CoarTag>
                </div>
                <div class="text-xs text-surface-500">
                  {{ relativeTime(s.LastActiveAt) }}
                </div>
              </div>
            </div>
            <div v-if="extraSessionCount > 0" class="text-xs text-surface-500 px-1">
              {{ t('dashboard.sessions.more', { n: extraSessionCount }, '+{n} weitere') }}
            </div>
          </div>

          <div class="pt-1">
            <button type="button" class="text-sm text-blue-600 hover:underline" @click="goToProfileSessions">
              {{ t('dashboard.sessions.cta', {}, 'Sessions verwalten →') }}
            </button>
          </div>
        </div>
      </CoarCard>
    </div>

    <!-- Admin cards — only rendered for users with at least one admin
         permission. Stays out of the way for normal end users so the
         personal section above doesn't look orphaned at the top. -->
    <template v-if="isAdmin">
      <div class="dashboard-grid">
        <!-- ─── Realm-Übersicht ─── -->
        <CoarCard elevated>
          <div class="p-6 space-y-4">
            <div class="flex items-center gap-3">
              <CoarIcon name="building-2" size="m" class="text-surface-500" />
              <h2 class="text-lg font-semibold">
                {{ t('dashboard.realmOverview.title', {}, 'Realm-Übersicht') }}
              </h2>
            </div>

            <div class="grid grid-cols-2 gap-3">
              <button
                type="button"
                class="dashboard-stat"
                :disabled="userCount === null && !userCountError"
                @click="goToUsers"
              >
                <div class="dashboard-stat__value">
                  <CoarSpinner v-if="userCount === null && !userCountError" size="s" />
                  <span v-else-if="userCountError">–</span>
                  <span v-else>{{ userCount }}</span>
                </div>
                <div class="dashboard-stat__label">
                  {{ t('dashboard.realmOverview.users', {}, 'Benutzer') }}
                </div>
              </button>

              <button
                type="button"
                class="dashboard-stat"
                :disabled="failedLogins24h === null && authLogLoading"
                @click="goToAuthLog"
              >
                <div
                  class="dashboard-stat__value"
                  :class="{ 'text-amber-600': (failedLogins24h ?? 0) > 0 }"
                >
                  <CoarSpinner v-if="authLogLoading" size="s" />
                  <span v-else-if="failedLogins24h === null">–</span>
                  <span v-else>{{ failedLogins24h }}</span>
                </div>
                <div class="dashboard-stat__label">
                  {{ t('dashboard.realmOverview.failedLogins24h', {}, 'Fehlversuche (24h)') }}
                </div>
              </button>
            </div>
          </div>
        </CoarCard>

        <!-- ─── Pending Change Requests ─── -->
        <CoarCard elevated>
          <div class="p-6 space-y-4">
            <div class="flex items-center gap-3">
              <CoarIcon name="inbox" size="m" class="text-surface-500" />
              <h2 class="text-lg font-semibold">
                {{ t('dashboard.pendingChangeRequests.title', {}, 'Offene Anfragen') }}
              </h2>
              <CoarTag
                v-if="!changeRequestsLoading && !changeRequestsError"
                :variant="changeRequests.length > 0 ? 'warning' : 'neutral'"
                size="s"
                class="ml-auto"
              >
                {{ changeRequests.length }}
              </CoarTag>
            </div>

            <div v-if="changeRequestsLoading" class="flex justify-center py-4">
              <CoarSpinner size="m" />
            </div>
            <CoarNote v-else-if="changeRequestsError" variant="error">
              {{ t('dashboard.errors.loadFailed', {}, 'Daten konnten nicht geladen werden.') }}
            </CoarNote>
            <div v-else-if="changeRequests.length === 0" class="text-sm text-surface-400">
              {{ t('dashboard.pendingChangeRequests.none', {}, 'Keine offenen Anfragen.') }}
            </div>
            <ul v-else class="space-y-1">
              <li
                v-for="r in topPendingRequests"
                :key="r.Id"
                class="flex items-center gap-2 text-sm rounded px-2 py-1 hover:bg-surface-50 cursor-pointer"
                @click="goToChangeRequests"
              >
                <span class="font-medium truncate flex-1">{{ r.UserLabel }}</span>
                <span class="text-xs text-surface-500">{{ changeRequestTypeLabel(r.Type) }}</span>
                <CoarTag
                  :variant="r.Status === 'AdminApprovalPending' ? 'warning' : 'neutral'"
                  size="s"
                >
                  {{ changeRequestStatusLabel(r.Status) }}
                </CoarTag>
              </li>
            </ul>

            <div class="pt-1">
              <button type="button" class="text-sm text-blue-600 hover:underline" @click="goToChangeRequests">
                {{ t('dashboard.pendingChangeRequests.cta', {}, 'Alle anzeigen →') }}
              </button>
            </div>
          </div>
        </CoarCard>

        <!-- ─── System-Aktivität ─── -->
        <CoarCard elevated>
          <div class="p-6 space-y-4">
            <div class="flex items-center gap-3">
              <CoarIcon name="scroll-text" size="m" class="text-surface-500" />
              <h2 class="text-lg font-semibold">
                {{ t('dashboard.systemActivity.title', {}, 'System-Aktivität') }}
              </h2>
            </div>

            <div v-if="authLogLoading" class="flex justify-center py-4">
              <CoarSpinner size="m" />
            </div>
            <CoarNote v-else-if="authLogError" variant="error">
              {{ t('dashboard.errors.loadFailed', {}, 'Daten konnten nicht geladen werden.') }}
            </CoarNote>
            <div v-else-if="authLog.length === 0" class="text-sm text-surface-400">
              {{ t('dashboard.systemActivity.none', {}, 'Noch keine Ereignisse.') }}
            </div>
            <ul v-else class="space-y-1">
              <li
                v-for="(e, idx) in recentSystemActivity"
                :key="idx"
                class="flex items-center gap-2 text-sm rounded px-2 py-1"
              >
                <CoarTag :variant="authLogLevelVariant(e.Level)" size="s">{{ e.Level }}</CoarTag>
                <span class="flex-1 truncate">{{ e.Message }}</span>
                <span v-if="e.UserName" class="text-xs text-surface-500 truncate max-w-[8rem]">
                  {{ e.UserName }}
                </span>
                <span class="text-xs text-surface-400 whitespace-nowrap">
                  {{ relativeTime(e.Timestamp) }}
                </span>
              </li>
            </ul>

            <div class="pt-1">
              <button type="button" class="text-sm text-blue-600 hover:underline" @click="goToAuthLog">
                {{ t('dashboard.systemActivity.cta', {}, 'Vollständig anzeigen →') }}
              </button>
            </div>
          </div>
        </CoarCard>

        <!-- ─── Login-Provider-Status ─── -->
        <CoarCard elevated>
          <div class="p-6 space-y-4">
            <div class="flex items-center gap-3">
              <CoarIcon name="log-in" size="m" class="text-surface-500" />
              <h2 class="text-lg font-semibold">
                {{ t('dashboard.loginProviderStatus.title', {}, 'Login-Provider') }}
              </h2>
            </div>

            <div v-if="providersLoading" class="flex justify-center py-4">
              <CoarSpinner size="m" />
            </div>
            <CoarNote v-else-if="providersError" variant="error">
              {{ t('dashboard.errors.loadFailed', {}, 'Daten konnten nicht geladen werden.') }}
            </CoarNote>
            <div v-else-if="loginProviders.length === 0" class="text-sm text-surface-400">
              {{ t('dashboard.loginProviderStatus.none', {}, 'Keine Provider eingerichtet.') }}
            </div>
            <ul v-else class="space-y-1">
              <li
                v-for="p in loginProviders"
                :key="p.Id"
                class="flex items-center gap-2 text-sm rounded px-2 py-1 hover:bg-surface-50 cursor-pointer"
                @click="goToLoginProvider(p.Id)"
              >
                <span
                  class="inline-block w-2 h-2 rounded-full flex-shrink-0"
                  :class="p.Enabled ? 'bg-green-500' : 'bg-surface-300'"
                  :title="p.Enabled
                    ? t('dashboard.loginProviderStatus.enabled', {}, 'Aktiviert')
                    : t('dashboard.loginProviderStatus.disabled', {}, 'Deaktiviert')"
                />
                <span class="flex-1 truncate">{{ p.DisplayName }}</span>
                <CoarTag v-if="p.Type === 'Internal'" variant="neutral" size="s">
                  {{ t('dashboard.loginProviderStatus.system', {}, 'System') }}
                </CoarTag>
                <span v-else class="text-xs text-surface-400">{{ p.Flavor }}</span>
              </li>
            </ul>

            <div class="pt-1">
              <button type="button" class="text-sm text-blue-600 hover:underline" @click="goToLoginProviders">
                {{ t('dashboard.loginProviderStatus.cta', {}, 'Verwalten →') }}
              </button>
            </div>
          </div>
        </CoarCard>
      </div>
    </template>
  </div>
</template>

<style scoped>
/* Three-column on lg+, two-column on md, single column on mobile —
   matches the existing profile-card-grid feel but with slightly looser
   minimums so admin cards (auth-log, change-requests) don't squeeze to the
   point of truncating every row. */
.dashboard-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 1.5rem;
}

@media (min-width: 768px) {
  .dashboard-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (min-width: 1280px) {
  .dashboard-grid {
    grid-template-columns: repeat(3, 1fr);
  }
}

.session-current {
  border-color: var(--coar-border-semantic-success, #86efac) !important;
  background: var(--coar-background-semantic-success-subtle, #f0fdf4) !important;
}

/* Mini stat block — clickable, no button chrome, but lights up on hover so
   the admin learns the numbers are drill-downs. Keeps the visual weight of
   a CoarCard interior without nesting another card inside one. */
.dashboard-stat {
  text-align: left;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: 6px;
  padding: 0.75rem 1rem;
  background: var(--coar-background-neutral-primary, #fff);
  cursor: pointer;
  transition: background-color 0.12s ease, border-color 0.12s ease;
}
.dashboard-stat:hover:not(:disabled) {
  background: var(--coar-background-neutral-secondary, #f7f7f7);
  border-color: var(--coar-border-neutral-tertiary, #d1d5db);
}
.dashboard-stat:disabled {
  cursor: default;
}
.dashboard-stat__value {
  font-size: 1.5rem;
  font-weight: 600;
  line-height: 1.2;
  display: flex;
  align-items: center;
  min-height: 1.8rem;
}
.dashboard-stat__label {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  margin-top: 0.125rem;
}
</style>
