<script setup lang="ts">
import { computed, onMounted, watch, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarIcon, CoarSpinner, CoarNote, CoarTag } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useHttpClient } from '@/composables/useHttpClient'
import { useAuthStore } from '@/stores/auth.store'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { ClientSessionDto, SessionDto, SessionListDto } from '@/models/session'
import type { UserDto } from '@/models/user'
import type { KpiTile } from './kpiTile'
import KpiCard from './KpiCard.vue'

const { t, language } = useI18n()
const router = useRouter()
const authStore = useAuthStore()
const loginProviderStore = useLoginProviderStore()

// ─── Per-card permission gates ───────────────────────────────────────────
// Each admin card/tile is gated on the *specific* permission that backs its
// data fetch — mirrors how AdminView's sidebar items work and avoids the bug
// where a help-desk user (who has user:read + auth-log:read but nothing else)
// would see the full admin face just because she holds *some* admin perm.
const canSeeUsers = computed(() => authStore.hasPermission('user:read'))
const canSeeAuthLog = computed(() => authStore.hasPermission('auth-log:read'))
const canSeeChangeRequests = computed(() => authStore.hasPermission('user:write'))
const canSeeLoginProviders = computed(() => authStore.hasPermission('login-provider:read'))

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
  /** While the underlying data isn't loaded yet we don't want to flash a
   *  red "missing" state — keep the row in a neutral pending state. */
  pending?: boolean
  label: string
  /** Pastel tag tone used in the list — green = OK, amber = missing,
   *  rose = expired/error. We never use rose for the security checklist
   *  today, but keeping the union open mirrors the task brief. */
  tagVariant: 'success' | 'warning' | 'error' | 'neutral'
  tagLabel: string
}

const securityItems = computed<SecurityItem[]>(() => {
  const items: SecurityItem[] = []

  const hasEmail = !!authStore.user?.Email
  items.push({
    key: 'email',
    ok: hasEmail,
    label: t('dashboard.security.emailLabel', {}, 'E-Mail hinterlegt'),
    tagVariant: hasEmail ? 'success' : 'warning',
    tagLabel: hasEmail
      ? t('dashboard.security.statusOk', {}, 'OK')
      : t('dashboard.security.statusMissing', {}, 'fehlt'),
  })

  const has2fa = authStore.user?.Has2FA === true
  items.push({
    key: 'mfa',
    ok: has2fa,
    label: t('dashboard.security.mfaLabel', {}, 'Zwei-Faktor-Authentisierung'),
    tagVariant: has2fa ? 'success' : 'warning',
    tagLabel: has2fa
      ? t('dashboard.security.statusOk', {}, 'OK')
      : t('dashboard.security.statusMissing', {}, 'fehlt'),
  })

  const passkeyPending = passkeyCount.value === null && !passkeysError.value
  const hasPasskey = (passkeyCount.value ?? 0) > 0
  items.push({
    key: 'passkey',
    ok: hasPasskey,
    pending: passkeyPending,
    label: t('dashboard.security.passkeyLabel', {}, 'Passkey hinterlegt'),
    tagVariant: passkeyPending ? 'neutral' : (hasPasskey ? 'success' : 'warning'),
    tagLabel: passkeyPending
      ? '…'
      : (hasPasskey
        ? t('dashboard.security.statusOk', {}, 'OK')
        : t('dashboard.security.statusMissing', {}, 'fehlt')),
  })

  return items
})

const securityScoreReady = computed(() =>
  passkeyCount.value !== null || passkeysError.value,
)
const securityScoreActive = computed(() => securityItems.value.filter(i => i.ok).length)
const securityScoreTotal = computed(() => securityItems.value.length)
const securityScoreAllGood = computed(() =>
  securityScoreReady.value && securityScoreActive.value === securityScoreTotal.value,
)

function goToProfileSecurity() {
  router.push('/profile')
}

// ─── Aktive Sessions ──────────────────────────────────────────────────────
const sessionsHttp = useHttpClient('/api/auth/sessions')
const browserSessions = ref<SessionDto[]>([])
const clientSessions = ref<ClientSessionDto[]>([])
const sessionsLoading = ref(true)
const sessionsError = ref(false)

async function loadSessions() {
  try {
    const res = await sessionsHttp.get<SessionListDto>()
    browserSessions.value = res.Sessions ?? []
    clientSessions.value = res.ClientSessions ?? []
  } catch {
    sessionsError.value = true
  } finally {
    sessionsLoading.value = false
  }
}

type DashboardSession =
  | (SessionDto & { Kind: 'Browser' })
  | (ClientSessionDto & { Kind: 'Client' })

const sessions = computed<DashboardSession[]>(() => [
  ...browserSessions.value.map(s => ({ ...s, Kind: 'Browser' as const })),
  ...clientSessions.value.map(s => ({ ...s, Kind: 'Client' as const })),
].sort((a, b) => new Date(b.LastActiveAt).getTime() - new Date(a.LastActiveAt).getTime()))
const topSessions = computed(() => sessions.value.slice(0, 3))
const extraSessionCount = computed(() => Math.max(0, sessions.value.length - 3))

function deviceLabel(s: DashboardSession): string {
  if (s.Kind === 'Client')
    return s.ClientDisplayName || s.ClientId

  // KPI-style "Browser auf Gerät" — the screenshot's row label uses
  // "Chrome auf Windows" rather than the older "Browser · OS" form.
  const browser = s.Browser || t('dashboard.sessions.unknownBrowser', {}, 'Browser')
  const os = s.OperatingSystem || s.DeviceType || t('dashboard.sessions.unknownDevice', {}, 'Unknown Device')
  return t('dashboard.sessions.deviceLabel', { browser, os }, '{browser} auf {os}')
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

// ─── Admin: User count ──────────────────────────────────────────────────
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

const activeOidcProviderCount = computed(() =>
  loginProviders.value.filter(p => p.Type === 'Oidc' && p.Enabled).length,
)

function goToLoginProvider(id: string) {
  // Provider detail is a URL-fragment-routed modal mounted by LoginProviderList
  // (`useRoutedModals()` reads the hash). Navigate to the list with the id in
  // the hash so the modal pops on arrival, matching the in-page click behavior.
  router.push({ path: '/admin/login-providers', hash: `#${id}` })
}

// ─── KPI tiles ────────────────────────────────────────────────────────────
// Tiles are split into two declarative lists — personal (always shown) and
// operational (permission-gated) — each rendered in its own labelled section
// below. KpiTile/TileTone live in ./kpiTile; the card markup in ./KpiCard.vue.
const personalKpiTiles = computed<KpiTile[]>(() => {
  const tiles: KpiTile[] = []

  // Personal tiles ---------------------------------------------------------
  const sessionsValue = sessionsError.value
    ? '–'
    : (sessionsLoading.value ? null : String(sessions.value.length))
  tiles.push({
    key: 'activeSessions',
    icon: 'monitor',
    tone: 'sky',
    value: sessionsValue,
    loading: sessionsLoading.value && !sessionsError.value,
    caption: t('dashboard.kpi.activeSessions', {}, 'Active Sessions'),
    onClick: goToProfileSessions,
  })

  // Security score: render once we know the passkey count (or its error).
  // Until then we leave value null so the tile shows the placeholder dash.
  const scoreValue = securityScoreReady.value
    ? `${securityScoreActive.value} / ${securityScoreTotal.value}`
    : null
  tiles.push({
    key: 'securityScore',
    icon: 'shield',
    tone: securityScoreAllGood.value ? 'emerald' : 'amber',
    value: scoreValue,
    warn: securityScoreReady.value && !securityScoreAllGood.value,
    loading: !securityScoreReady.value,
    caption: t('dashboard.kpi.securityScore', {}, 'Sicherheits-Score'),
    onClick: goToProfileSecurity,
  })

  return tiles
})

// Operational tiles — each gated on its own permission. Rendered in the separate
// "Realm-Betrieb" band so they never share a row with the personal tiles.
const opsKpiTiles = computed<KpiTile[]>(() => {
  const tiles: KpiTile[] = []

  if (canSeeUsers.value) {
    const userCountValue = userCountError.value
      ? '–'
      : (userCount.value === null ? null : String(userCount.value))
    tiles.push({
      key: 'userCount',
      icon: 'users',
      tone: 'violet',
      value: userCountValue,
      loading: userCount.value === null && !userCountError.value,
      caption: t('dashboard.kpi.userCount', {}, 'User im Realm'),
      onClick: goToUsers,
    })
  }

  if (canSeeAuthLog.value) {
    const failed = failedLogins24h.value
    const failedValue = authLogError.value
      ? '–'
      : (authLogLoading.value ? null : String(failed ?? 0))
    tiles.push({
      key: 'failedLast24h',
      icon: 'shield-alert',
      tone: 'rose',
      value: failedValue,
      bad: !authLogLoading.value && !authLogError.value && (failed ?? 0) > 0,
      loading: authLogLoading.value && !authLogError.value,
      caption: t('dashboard.kpi.failedLast24h', {}, 'Fehlversuche 24h'),
      onClick: goToAuthLog,
    })
  }

  if (canSeeChangeRequests.value) {
    const pendingValue = changeRequestsError.value
      ? '–'
      : (changeRequestsLoading.value ? null : String(changeRequests.value.length))
    const pendingCount = changeRequests.value.length
    tiles.push({
      key: 'pendingChangeRequests',
      icon: 'inbox',
      tone: 'amber',
      value: pendingValue,
      warn: !changeRequestsLoading.value && !changeRequestsError.value && pendingCount > 0,
      loading: changeRequestsLoading.value && !changeRequestsError.value,
      caption: t('dashboard.kpi.pendingChangeRequests', {}, 'Offene Anfragen'),
      onClick: goToChangeRequests,
    })
  }

  if (canSeeLoginProviders.value) {
    const providersValue = providersError.value
      ? '–'
      : (providersLoading.value ? null : String(activeOidcProviderCount.value))
    tiles.push({
      key: 'activeLoginProviders',
      icon: 'log-in',
      tone: 'blue',
      value: providersValue,
      loading: providersLoading.value && !providersError.value,
      caption: t('dashboard.kpi.activeLoginProviders', {}, 'Active Login Providers'),
      onClick: () => router.push('/admin/login-providers'),
    })
  }

  return tiles
})

// Personal pair is always exactly 2 → always the centered --user grid. The ops
// band fans out to the wide grid once it has >2 tiles, otherwise stays centered
// so a lone ops tile doesn't float alone in a 6-column row.
const opsGridWide = computed(() => opsKpiTiles.value.length > 2)

// Show the second list-card row (System-Aktivität / Login-Provider) only if at
// least one of those cards is visible to the viewer. Both are gated below too,
// so the row container collapses cleanly when nothing's visible.
const showAdminListRow = computed(() => canSeeAuthLog.value || canSeeLoginProviders.value)

// The whole "Realm-Betrieb" band (heading + ops KPI grid + ops list cards) shows
// only if the viewer has at least one ops tile or list card; otherwise it
// collapses entirely (no dangling heading) for personal-only viewers.
const hasOpsSection = computed(() => opsKpiTiles.value.length > 0 || showAdminListRow.value)

// (KPI card markup + tone palette now live in ./KpiCard.vue.)

// ─── Bootstrap ────────────────────────────────────────────────────────────
onMounted(() => {
  // Personal cards — always.
  loadPasskeys()
  loadSessions()

  // Admin cards — fire each fetch only when the viewer actually holds the
  // permission that backs it. Skipping these for non-eligible users avoids
  // 403 noise and prevents the bug where a help-desk user (who only holds
  // user:read + auth-log:read) would see the full admin face.
  if (canSeeUsers.value) loadUserCount()
  if (canSeeAuthLog.value) loadAuthLog()
  if (canSeeChangeRequests.value) loadChangeRequests()
  if (canSeeLoginProviders.value) loadLoginProviders()
})
</script>

<template>
  <div class="w-full py-6 space-y-6">
    <!-- ─── Section: Mein Konto (personal) ─── -->
    <section class="dashboard-section">
      <h2 class="dashboard-section__heading">{{ t('dashboard.section.personal', {}, 'My account') }}</h2>

      <!-- Personal KPI tiles — always exactly two → centered pair. -->
      <div class="kpi-grid kpi-grid--user">
        <KpiCard v-for="tile in personalKpiTiles" :key="tile.key" :tile="tile" />
      </div>

      <!-- Personal list cards: Account-Sicherheit + Aktive Sessions -->
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <!-- Account-Sicherheit -->
        <CoarCard elevated>
          <div class="p-4">
            <h3 class="list-card__heading">
              <CoarIcon name="shield-check" size="s" class="mr-1 inline-block align-text-bottom" />
              {{ t('dashboard.security.title', {}, 'Account Security') }}
            </h3>
            <div class="space-y-1">
              <button
                v-for="item in securityItems"
                :key="item.key"
                type="button"
                class="list-row"
                :class="{ 'list-row--strong': !item.ok && !item.pending }"
                @click="goToProfileSecurity"
              >
                <span class="list-row__label">{{ item.label }}</span>
                <span class="list-row__meta">
                  <CoarSpinner v-if="item.pending" size="s" />
                  <CoarTag v-else :variant="item.tagVariant" size="s">
                    {{ item.tagLabel }}
                  </CoarTag>
                </span>
              </button>
            </div>
            <div class="list-card__footer">
              <button type="button" class="list-card__cta" @click="goToProfileSecurity">
                {{ t('dashboard.security.cta', {}, 'To Profile →') }}
              </button>
            </div>
          </div>
        </CoarCard>

        <!-- Aktive Sessions -->
        <CoarCard elevated>
          <div class="p-4">
            <h3 class="list-card__heading">
              <CoarIcon name="monitor" size="s" class="mr-1 inline-block align-text-bottom" />
              {{ t('dashboard.sessions.title', {}, 'Active Sessions') }}
            </h3>
            <div v-if="sessionsLoading" class="list-card__loading">
              <CoarSpinner size="m" />
            </div>
            <CoarNote v-else-if="sessionsError" variant="error">
              {{ t('dashboard.errors.loadFailed', {}, 'Failed to load the data.') }}
            </CoarNote>
            <div v-else-if="sessions.length === 0" class="list-card__empty">
              {{ t('dashboard.sessions.none', {}, 'No sessions.') }}
            </div>
            <div v-else class="space-y-1">
              <button
                v-for="s in topSessions"
                :key="s.Id"
                type="button"
                class="list-row"
                :class="{ 'list-row--strong': s.Kind === 'Browser' && s.IsCurrent }"
                @click="goToProfileSessions"
              >
                <span class="list-row__label">{{ deviceLabel(s) }}</span>
                <span class="list-row__meta">
                  <CoarTag v-if="s.Kind === 'Browser' && s.IsCurrent" variant="success" size="s">
                    {{ t('dashboard.sessions.thisDevice', {}, 'This Device') }}
                  </CoarTag>
                  <CoarTag v-else-if="s.Kind === 'Client'" variant="neutral" size="s">
                    {{ t('dashboard.sessions.app', {}, 'App') }}
                  </CoarTag>
                  <CoarTag variant="neutral" size="s">{{ relativeTime(s.LastActiveAt) }}</CoarTag>
                </span>
              </button>
              <div v-if="extraSessionCount > 0" class="list-row__more">
                {{ t('dashboard.sessions.more', { n: extraSessionCount }, '+{n} weitere') }}
              </div>
            </div>
            <div class="list-card__footer">
              <button type="button" class="list-card__cta" @click="goToProfileSessions">
                {{ t('dashboard.sessions.cta', {}, 'Sessions verwalten →') }}
              </button>
            </div>
          </div>
        </CoarCard>
      </div>
    </section>

    <!-- ─── Section: Realm-Betrieb (operational) ─── -->
    <section v-if="hasOpsSection" class="dashboard-section">
      <h2 class="dashboard-section__heading">{{ t('dashboard.section.operations', {}, 'Realm operations') }}</h2>

      <!-- Operational KPI tiles -->
      <div v-if="opsKpiTiles.length" class="kpi-grid" :class="opsGridWide ? 'kpi-grid--admin' : 'kpi-grid--user'">
        <KpiCard v-for="tile in opsKpiTiles" :key="tile.key" :tile="tile" />
      </div>

      <!-- Operational list cards: System-Aktivität + Login-Provider -->
      <template v-if="showAdminListRow">
        <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <!-- System-Aktivität (last 10 AuthLog rows) -->
          <CoarCard v-if="canSeeAuthLog" elevated>
            <div class="p-4">
              <h3 class="list-card__heading">
                <CoarIcon name="scroll-text" size="s" class="mr-1 inline-block align-text-bottom" />
                {{ t('dashboard.systemActivity.title', {}, 'System Activity') }}
              </h3>
              <div v-if="authLogLoading" class="list-card__loading">
                <CoarSpinner size="m" />
              </div>
              <CoarNote v-else-if="authLogError" variant="error">
                {{ t('dashboard.errors.loadFailed', {}, 'Failed to load the data.') }}
              </CoarNote>
              <div v-else-if="authLog.length === 0" class="list-card__empty">
                {{ t('dashboard.systemActivity.none', {}, 'No events yet.') }}
              </div>
              <div v-else class="space-y-1">
                <button
                  v-for="(e, idx) in recentSystemActivity"
                  :key="idx"
                  type="button"
                  class="list-row"
                  :class="{ 'list-row--strong': e.Level === 'Error' }"
                  @click="goToAuthLog"
                >
                  <span class="list-row__label">
                    <span class="list-row__title">{{ e.Message }}</span>
                    <span v-if="e.UserName" class="list-row__sub">{{ e.UserName }}</span>
                  </span>
                  <span class="list-row__meta">
                    <CoarTag :variant="authLogLevelVariant(e.Level)" size="s">
                      {{ relativeTime(e.Timestamp) }}
                    </CoarTag>
                  </span>
                </button>
              </div>
              <div class="list-card__footer">
                <button type="button" class="list-card__cta" @click="goToAuthLog">
                  {{ t('dashboard.systemActivity.cta', {}, 'View all →') }}
                </button>
              </div>
            </div>
          </CoarCard>

          <!-- Login-Provider -->
          <CoarCard v-if="canSeeLoginProviders" elevated>
            <div class="p-4">
              <h3 class="list-card__heading">
                <CoarIcon name="log-in" size="s" class="mr-1 inline-block align-text-bottom" />
                {{ t('dashboard.loginProviderStatus.title', {}, 'Login-Provider') }}
              </h3>
              <div v-if="providersLoading" class="list-card__loading">
                <CoarSpinner size="m" />
              </div>
              <CoarNote v-else-if="providersError" variant="error">
                {{ t('dashboard.errors.loadFailed', {}, 'Failed to load the data.') }}
              </CoarNote>
              <div v-else-if="loginProviders.length === 0" class="list-card__empty">
                {{ t('dashboard.loginProviderStatus.none', {}, 'No providers configured.') }}
              </div>
              <div v-else class="space-y-1">
                <button
                  v-for="p in loginProviders"
                  :key="p.Id"
                  type="button"
                  class="list-row"
                  @click="goToLoginProvider(p.Id)"
                >
                  <span class="list-row__label">
                    <span
                      class="provider-dot"
                      :class="p.Enabled ? 'provider-dot--on' : 'provider-dot--off'"
                      :title="p.Enabled
                        ? t('dashboard.loginProviderStatus.enabled', {}, 'Enabled')
                        : t('dashboard.loginProviderStatus.disabled', {}, 'Disabled')"
                    />
                    <span class="list-row__title">{{ p.DisplayName }}</span>
                  </span>
                  <span class="list-row__meta">
                    <!-- Text status next to the colour dot (status not by colour alone). -->
                    <CoarTag :variant="p.Enabled ? 'success' : 'neutral'" size="s">
                      {{ p.Enabled
                        ? t('dashboard.loginProviderStatus.enabled', {}, 'Enabled')
                        : t('dashboard.loginProviderStatus.disabled', {}, 'Disabled') }}
                    </CoarTag>
                    <CoarTag v-if="p.Type === 'Internal'" variant="neutral" size="s">
                      {{ t('dashboard.loginProviderStatus.system', {}, 'System') }}
                    </CoarTag>
                    <CoarTag v-else variant="info" size="s">{{ p.Flavor }}</CoarTag>
                  </span>
                </button>
              </div>
            </div>
          </CoarCard>
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
/* KPI grid: 2-column for end-users, fans out to 6-column on lg viewports
   when admin tiles are present. */
.kpi-grid {
  display: grid;
  gap: 1rem;
  grid-template-columns: repeat(2, minmax(0, 1fr));
}
@media (min-width: 1024px) {
  .kpi-grid--user { grid-template-columns: repeat(2, minmax(0, 1fr)); max-width: 32rem; margin: 0 auto; }
  .kpi-grid--admin { grid-template-columns: repeat(6, minmax(0, 1fr)); }
}

/* Dashboard section bands — "Mein Konto" / "Realm-Betrieb". The wrapping
   <section> groups its heading with its own grids/cards so the page-level
   space-y-6 gap falls BETWEEN the two bands, while inside a band the heading sits
   tight above its content. (KPI card styles now live in ./KpiCard.vue.) */
.dashboard-section > * + * {
  margin-top: 1rem;
}
.dashboard-section__heading {
  font-size: 1.0625rem;
  font-weight: 600;
  color: var(--coar-text-neutral-primary, #111827);
}
/* Tighten the heading → first-content gap (wins over the 1rem rule above by
   source order at equal specificity). */
.dashboard-section__heading + * {
  margin-top: 0.5rem;
}

/* List card heading: ALL-CAPS small text + leading icon. */
.list-card__heading {
  font-size: 0.875rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-bottom: 0.75rem;
}

/* List rows: button-based, hover-tinted. */
.list-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  width: 100%;
  padding: 0.5rem 0.5rem;
  border-radius: 0.375rem;
  text-align: left;
  font-size: 0.875rem;
  transition: background-color 0.1s;
  cursor: pointer;
  border: none;
  background: none;
  color: inherit;
}
.list-row:hover {
  background-color: var(--coar-background-neutral-tertiary, rgba(0, 0, 0, 0.04));
}
.list-row--strong .list-row__title,
.list-row--strong > .list-row__label {
  font-weight: 600;
}

.list-row__label {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.list-row__title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.list-row__sub {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-left: 0.5rem;
  flex-shrink: 0;
}
.list-row__meta {
  display: inline-flex;
  align-items: center;
  gap: 0.375rem;
  flex-shrink: 0;
  font-size: 0.75rem;
}
.list-row__more {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #9ca3af);
  padding: 0.25rem 0.5rem;
}

.list-card__loading {
  display: flex;
  justify-content: center;
  padding: 1rem 0;
}
.list-card__empty {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary, #9ca3af);
  padding: 0.5rem 0.5rem;
  text-align: center;
}
.list-card__footer {
  margin-top: 0.5rem;
  padding: 0 0.5rem;
}
.list-card__cta {
  font-size: 0.8125rem;
  color: var(--coar-text-link, #2563eb);
  background: transparent;
  border: 0;
  padding: 0;
  cursor: pointer;
}
.list-card__cta:hover {
  text-decoration: underline;
}

.provider-dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 9999px;
  flex-shrink: 0;
}
.provider-dot--on  { background: #22c55e; }
.provider-dot--off { background: var(--coar-border-neutral-tertiary, #d1d5db); }
</style>
