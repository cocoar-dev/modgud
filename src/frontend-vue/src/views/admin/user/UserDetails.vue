<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useUserStore, type UserGroupDto, type InheritedUserGroupDto, type EffectiveGroupDto, type EffectiveGroupDiagnostic } from '@/stores/user.store'
import { useGroupStore } from '@/stores/group.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { CoarTextInput, CoarFormField, CoarIcon, CoarTabGroup, CoarTab, CoarListbox, CoarDualListbox, CoarButton, CoarCheckbox, CoarNote, CoarTag } from '@cocoar/vue-ui'
import type { CoarListboxOption } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const userStore = useUserStore()
const groupStore = useGroupStore()
const appConfig = useAppConfigStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const activeTab = ref<'general' | 'groups' | 'effective' | 'security'>('general')

// Security info (2FA methods + grace due date) — loaded with user profile.
const securityInfo = ref<{
  Has2FA: boolean
  TwoFactorMethods: string[]
  SecureSetupDueAt: string | null
  GracePeriodDaysOverride: number | null
  TwoFactorExempt: boolean
} | null>(null)
const graceBusy = ref(false)

// Local editable state for the Sicherheit-Tab. The main "Speichern" button commits any
// changes to these in a single save() call. We track originals so we can skip the
// backend write when nothing changed.
const overrideInput = ref<string>('')  // empty = no override / fall back to global default
const exemptLocal = ref<boolean>(false)
const originalOverride = ref<number | null>(null)
const originalExempt = ref<boolean>(false)

const graceDaysRemaining = computed(() => {
  if (!securityInfo.value?.SecureSetupDueAt) return null
  const ms = new Date(securityInfo.value.SecureSetupDueAt).getTime() - Date.now()
  if (ms <= 0) return 0
  return Math.max(1, Math.ceil(ms / (1000 * 60 * 60 * 24)))
})

const parsedOverride = computed<number | null>(() => {
  const raw = overrideInput.value.trim()
  if (raw === '') return null
  const n = Number.parseInt(raw, 10)
  return Number.isFinite(n) ? Math.max(0, n) : null
})

const policyDirty = computed(() => {
  if (!securityInfo.value) return false
  return parsedOverride.value !== originalOverride.value
    || exemptLocal.value !== originalExempt.value
})

async function resetGrace() {
  if (graceBusy.value) return
  graceBusy.value = true
  try {
    const due = await userStore.resetGrace(props.id)
    if (securityInfo.value) securityInfo.value.SecureSetupDueAt = due
  } finally {
    graceBusy.value = false
  }
}

async function clearGrace() {
  if (graceBusy.value) return
  graceBusy.value = true
  try {
    await userStore.clearGrace(props.id)
    // Backend now sets DueAt to "now" (past), so grace is expired but still tracked
    if (securityInfo.value) securityInfo.value.SecureSetupDueAt = new Date().toISOString()
  } finally {
    graceBusy.value = false
  }
}

// Group membership state (lazy loaded when entering the Groups tab).
const directGroups = ref<UserGroupDto[]>([])
const inheritedGroups = ref<InheritedUserGroupDto[]>([])
const effectiveGroups = ref<EffectiveGroupDto[]>([])
const effectiveDiagnostics = ref<EffectiveGroupDiagnostic[]>([])
const groupsLoaded = ref(false)
const groupsSaving = ref(false)

async function loadGroups() {
  if (isCreate.value) return
  // Both endpoints in parallel — independent reads, both per-tab one-shot.
  const [data, eff] = await Promise.all([
    userStore.getGroups(props.id),
    userStore.getEffectiveGroups(props.id),
  ])
  directGroups.value = data.Direct
  inheritedGroups.value = data.Inherited
  effectiveGroups.value = eff.Groups
  effectiveDiagnostics.value = eff.Diagnostics
  groupsLoaded.value = true
}

// Both Gruppen and Effektiv tabs read from the same loadGroups() call
// (it pulls direct + inherited + effective in parallel) — load on the
// first switch to either tab so the user can hop between them without
// a re-fetch flicker.
watch(activeTab, (tab) => {
  if ((tab === 'groups' || tab === 'effective') && !groupsLoaded.value) loadGroups()
})

function effectiveSourceLabel(g: EffectiveGroupDto): string {
  if (g.Source === 'DirectManual') {
    return t('admin.userDetails.effectiveGroups.sourceDirect', {}, 'Direkt')
  }
  if (g.Source === 'InheritedManual') {
    // Show the entry direct group (first hop in the Via chain) so the admin
    // sees how the inherited membership was reached.
    const via = g.Via?.[0]?.Name ?? ''
    return t('admin.userDetails.effectiveGroups.sourceInherited', { via }, `Vererbt über ${via}`)
  }
  return t('admin.userDetails.effectiveGroups.sourceAuto', {}, 'Auto-Skript')
}

function effectiveSourceVariant(g: EffectiveGroupDto): 'neutral' | 'success' | 'info' | 'warning' {
  if (g.Source === 'DirectManual') return 'success'
  if (g.Source === 'InheritedManual') return 'info'
  return 'neutral'
}

// All assignable groups (non-Auto, non-deleted) as picker options.
const allGroupsOptions = computed<CoarListboxOption<string>[]>(() =>
  groupStore.groups
    .filter(g => g.MembershipMode !== 'Auto')
    .map(g => ({
      value: g.Id,
      label: g.Name,
      subtitle: g.Description || undefined,
      tooltip: g.Description || undefined,
      icon: 'users',
    }))
)

const directGroupIds = computed({
  get: () => directGroups.value.map(g => g.Id),
  set: async (newIds) => {
    const prev = directGroups.value.map(g => g.Id)
    const added = newIds.filter(id => !prev.includes(id))
    const removed = prev.filter(id => !newIds.includes(id))
    if (added.length === 0 && removed.length === 0) return
    groupsSaving.value = true
    try {
      for (const id of added) await userStore.addGroup(props.id, id)
      for (const id of removed) await userStore.removeGroup(props.id, id)
      await loadGroups() // refresh direct + inherited
    } finally {
      groupsSaving.value = false
    }
  },
})

const inheritedOptions = computed<CoarListboxOption<string>[]>(() =>
  inheritedGroups.value.map(g => ({
    value: g.Id,
    label: g.Name,
    subtitle: t('admin.userDetails.inheritedVia', { via: g.ViaName }, 'via: {via}'),
    tooltip: g.Description || undefined,
    icon: 'users',
  }))
)

// Profile form
const form = ref({
  Firstname: '',
  Lastname: '',
  Acronym: '',
  Email: '',
  UserName: '',
})

// Identity-side flag — admin override. Persisted via PUT when changed
// (or POST at create). Setting true also unblocks the user's forgot-password
// and self-magic-link, which are gated on this flag.
// Create-mode default: true. The admin is typing the address themselves
// right now, so they're vouching for it — same logic as the bootstrap-admin
// and invite-consume paths. Edit-mode reads the persisted value.
const emailConfirmed = ref(true)
const originalEmailConfirmed = ref(false)

const userNameError = ref('')

// Account state
const isActive = ref(true)
const originalActive = ref(true)

const modalTitle = computed(() => {
  const name = `${form.value.Firstname} ${form.value.Lastname}`.trim()
  const acronym = form.value.Acronym?.trim()
  if (name && acronym) return `${name} | ${acronym}`
  if (name) return name
  if (acronym) return acronym
  return isCreate.value ? t('admin.userDetails.createTitle', {}, 'Create User') : ''
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Firstname.trim()
    || !form.value.Lastname.trim()
    || !form.value.UserName.trim()
    || !form.value.Email.trim()
    || loading.value
    || graceBusy.value,
  onClick: save,
}))


onMounted(async () => {
  if (!isCreate.value) {
    loading.value = true
    try {
      const [user, sec] = await Promise.all([
        userStore.getById(props.id),
        userStore.getSecurityInfo(props.id),
        groupStore.initialize(),
      ])
      form.value = {
        Firstname: user.Firstname,
        Lastname: user.Lastname,
        Acronym: user.Acronym || '',
        Email: user.Email || '',
        UserName: user.UserName || '',
      }
      emailConfirmed.value = user.EmailConfirmed
      originalEmailConfirmed.value = user.EmailConfirmed
      isActive.value = user.IsActive
      originalActive.value = user.IsActive
      securityInfo.value = sec
      overrideInput.value = sec.GracePeriodDaysOverride?.toString() ?? ''
      exemptLocal.value = sec.TwoFactorExempt
      originalOverride.value = sec.GracePeriodDaysOverride
      originalExempt.value = sec.TwoFactorExempt
    } finally {
      loading.value = false
    }
  }
})

async function save() {
  if (!form.value.Firstname.trim()
      || !form.value.Lastname.trim()
      || !form.value.UserName.trim()
      || !form.value.Email.trim()) return
  loading.value = true
  try {
    if (isCreate.value) {
      await userStore.createEntity({
        Firstname: form.value.Firstname,
        Lastname: form.value.Lastname,
        Acronym: form.value.Acronym || undefined,
        Email: form.value.Email || undefined,
        UserName: form.value.UserName,
        EmailConfirmed: emailConfirmed.value || undefined,
      })
    } else {
      // Optimistic update — update store immediately with expected state
      const existing = userStore.getFromStore(props.id)
      if (existing) {
        userStore.setStoreEntities([{
          ...existing,
          Firstname: form.value.Firstname,
          Lastname: form.value.Lastname,
          Acronym: form.value.Acronym || undefined,
          Email: form.value.Email || undefined,
          UserName: form.value.UserName,
          IsActive: isActive.value,
          Status: 'Pending' as const,
        }])
      }

      // Single request — profile + active state + email-verified override.
      // Only emit EmailConfirmed when the admin actually changed it; otherwise
      // a no-op PUT could still rewrite (and audit-log) the flag.
      await userStore.httpClient.addPath(props.id).put({
        Firstname: form.value.Firstname,
        Lastname: form.value.Lastname,
        Acronym: form.value.Acronym || undefined,
        Email: form.value.Email || undefined,
        UserName: form.value.UserName,
        IsActive: isActive.value !== originalActive.value ? isActive.value : undefined,
        EmailConfirmed: emailConfirmed.value !== originalEmailConfirmed.value ? emailConfirmed.value : undefined,
      })

      // Grace policy (per-user override + exempt). Only write when something changed,
      // since this hits a separate endpoint and we don't want noise in the auth log.
      if (policyDirty.value) {
        await userStore.setGracePolicy(props.id, {
          // Sentinel -1 clears the per-user override on the backend; null would skip it.
          GracePeriodDaysOverride: parsedOverride.value === null ? -1 : parsedOverride.value,
          TwoFactorExempt: exemptLocal.value,
        })
      }
    }
    props.close()
  } catch (e: any) {
    if (e?.status === 409) {
      userNameError.value = t('admin.userDetails.usernameTaken', {}, 'Username is already taken')
    } else {
      throw e
    }
  } finally {
    loading.value = false
  }
}

watch(() => form.value.UserName, () => {
  userNameError.value = ''
})

</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="user" :footer-button="footerButton" width="42rem">
    <div v-if="!loading" class="flex flex-col min-w-0 min-h-0 flex-1">
      <CoarTabGroup v-if="!isCreate" v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.userDetails.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab id="groups">{{ t('admin.userDetails.tabs.groups', {}, 'Direkte Gruppen') }}</CoarTab>
        <CoarTab id="effective">{{ t('admin.userDetails.tabs.effective', {}, 'Effektiv') }}</CoarTab>
        <CoarTab id="security">{{ t('admin.userDetails.tabs.security', {}, 'Security') }}</CoarTab>
      </CoarTabGroup>

      <!-- Tab: General -->
      <div v-show="isCreate || activeTab === 'general'" class="tab-content">
        <section>
          <div class="flex flex-col gap-2">
            <div class="flex items-end gap-2">
              <CoarFormField :label="t('admin.users.firstname', {}, 'First Name')" required class="flex-1">
                <CoarTextInput v-model="form.Firstname" clearable />
              </CoarFormField>
              <CoarFormField :label="t('admin.users.lastname', {}, 'Last Name')" required class="flex-1">
                <CoarTextInput v-model="form.Lastname" clearable />
              </CoarFormField>
              <CoarFormField :label="t('admin.users.acronym', {}, 'Acronym')" class="w-20">
                <CoarTextInput v-model="form.Acronym" clearable />
              </CoarFormField>
            </div>
            <CoarFormField :label="t('admin.users.email', {}, 'Email')" required>
              <CoarTextInput v-model="form.Email" clearable />
              <div v-if="form.Email" class="email-verify-status">
                <CoarCheckbox v-model="emailConfirmed"
                  :label="t('admin.userDetails.emailVerifiedToggle', {}, 'Mark email address as verified')" />
                <p class="email-verify-hint">
                  {{ emailConfirmed
                      ? t('admin.userDetails.emailVerifiedHint', {}, 'Forgot-password and self-magic-link are unlocked for this user.')
                      : t('admin.userDetails.emailUnverifiedHint', {}, 'Forgot-password and self-magic-link are blocked until the user verifies their email.') }}
                </p>
              </div>
            </CoarFormField>
            <CoarFormField :label="t('admin.users.username', {}, 'Username')" required>
              <CoarTextInput v-model="form.UserName" clearable />
              <span v-if="userNameError" class="text-xs text-red-600">{{ userNameError }}</span>
            </CoarFormField>
            <div v-if="!isCreate" class="mt-1">
              <CoarCheckbox v-model="isActive"
                :label="t('admin.userDetails.activeCheckbox', {}, 'Benutzer aktiv')" />
            </div>
          </div>
        </section>
      </div>

      <!-- Tab: Security -->
      <div v-show="!isCreate && activeTab === 'security'" class="tab-content">
        <section v-if="securityInfo" class="flex flex-col gap-4 text-sm">
          <!-- 2FA status -->
          <div>
            <div class="section-heading">{{ t('admin.userDetails.twoFactorHeading', {}, 'Two-factor authentication') }}</div>
            <div class="flex items-center gap-2">
              <span class="text-gray-600">{{ t('admin.userDetails.twoFactor', {}, '2FA:') }}</span>
              <span v-if="securityInfo.Has2FA" class="status-badge status-active">
                <CoarIcon name="check" size="s" />
                {{ securityInfo.TwoFactorMethods.join(', ') }}
              </span>
              <span v-else-if="exemptLocal" class="status-badge status-exempt">
                <CoarIcon name="shield-alert" size="s" />
                {{ t('admin.userDetails.exemptBadge', {}, 'Ausgenommen — 2FA nicht erforderlich') }}
              </span>
              <span v-else class="status-badge status-inactive">
                <CoarIcon name="x" size="s" />
                {{ t('admin.userDetails.noTwoFactor', {}, 'Not configured') }}
              </span>
            </div>
          </div>

          <!-- Grace period — hidden when user has 2FA or is exempt -->
          <div v-if="!securityInfo.Has2FA && !exemptLocal && appConfig.config.AuthenticationMinimumLevel >= 1">
            <div class="section-heading">{{ t('admin.userDetails.graceHeading', {}, 'Grace period') }}</div>
            <CoarNote v-if="graceDaysRemaining === null" variant="info">
              {{ t('admin.userDetails.graceNotStarted', {}, 'Grace period starts on first login.') }}
            </CoarNote>
            <CoarNote v-else-if="graceDaysRemaining > 0" variant="warning">
              {{ t('admin.userDetails.graceRemaining', { days: graceDaysRemaining }, `${graceDaysRemaining} day(s) remaining.`) }}
            </CoarNote>
            <CoarNote v-else variant="error">
              {{ t('admin.userDetails.graceExpired', {}, 'Grace expired — next login forces 2FA setup.') }}
            </CoarNote>
            <div class="flex gap-2 mt-2">
              <CoarButton size="s" variant="primary" icon-start="rotate-ccw" :loading="graceBusy" @click="resetGrace">
                {{ t('admin.userDetails.resetGrace', { days: securityInfo.GracePeriodDaysOverride ?? appConfig.config.TwoFactorGracePeriodDays }, `Reset grace (+${securityInfo.GracePeriodDaysOverride ?? appConfig.config.TwoFactorGracePeriodDays}d)`) }}
              </CoarButton>
              <CoarButton v-if="securityInfo.SecureSetupDueAt" size="s" variant="danger" icon-start="shield-alert" :loading="graceBusy" @click="clearGrace">
                {{ t('admin.userDetails.clearGrace', {}, 'Force immediate enforcement') }}
              </CoarButton>
            </div>
          </div>

          <!-- Per-user policy overrides — committed together with the main Speichern button -->
          <div>
            <div class="section-heading">{{ t('admin.userDetails.policyHeading', {}, 'Individuelle Richtlinie') }}</div>
            <div class="flex flex-col gap-3">
              <!-- Grace days override -->
              <CoarFormField :label="t('admin.userDetails.policyDays', {}, 'Individuelle Frist in Tagen (leer = globaler Default)')">
                <CoarTextInput v-model="overrideInput" type="number"
                  :placeholder="t('admin.userDetails.policyDaysPlaceholder', { days: appConfig.config.TwoFactorGracePeriodDays }, `${appConfig.config.TwoFactorGracePeriodDays} (Default)`)"
                  :disabled="exemptLocal" />
              </CoarFormField>

              <!-- Exempt checkbox -->
              <div class="flex flex-col gap-1">
                <CoarCheckbox v-model="exemptLocal"
                  :label="t('admin.userDetails.exemptCheckbox', {}, 'Disable 2FA requirement for this user')" />
                <span class="text-xs text-gray-500 pl-6">
                  {{ t('admin.userDetails.exemptHint', {}, 'User bypasses grace period and enforcement entirely. For service accounts / legacy users.') }}
                </span>
              </div>
            </div>
          </div>
        </section>
      </div>

      <!-- Tab: Direct Groups — the editor surface. The admin picks who
           the user is a direct member of; everything else (inheritance,
           auto-script matches) is shown on the Effektiv tab. -->
      <div v-show="!isCreate && activeTab === 'groups'" class="tab-content">
        <section class="flex-section flex-1">
          <CoarDualListbox
            v-model="directGroupIds"
            :options="allGroupsOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.userDetails.availableGroups', {}, 'Available')"
            :selected-label="t('admin.userDetails.memberOf', {}, 'Member of')"
            :search-placeholder="t('admin.userDetails.searchGroups', {}, 'Search groups…')"
            :disabled="groupsSaving"
            class="flex-1 min-h-0"
          />
        </section>
      </div>

      <!-- Tab: Effective — read-only debug surface. Live effective
           membership (Direct/Inherited materialized + Auto-script
           matches), independent of MemberIds state. "Drift" warnings
           flag Auto rows where the predicate matches but the user is
           not in MemberIds — somebody never recomputed. -->
      <div v-show="!isCreate && activeTab === 'effective'" class="tab-content">
        <section v-if="effectiveGroups.length > 0 || effectiveDiagnostics.length > 0" class="flex-section">
          <div class="section-heading">
            {{ t('admin.userDetails.effectiveGroups.heading', {}, 'Effektive Mitgliedschaft') }}
          </div>
          <p class="tab-hint">
            {{ t('admin.userDetails.effectiveGroups.hint', {}, 'Materialisierte Sicht aller Gruppen die diesem User aktuell zugewiesen sind — direkt, geerbt über genestete Gruppen, oder per Auto-Skript.') }}
          </p>
          <div class="effective-list">
            <div v-for="g in effectiveGroups" :key="g.Id" class="effective-row">
              <CoarIcon name="users" size="s" class="effective-icon" />
              <div class="effective-name">
                <span>{{ g.Name }}</span>
                <span v-if="g.Roles.length > 0" class="effective-roles">
                  · {{ g.Roles.map(r => r.Name).join(', ') }}
                </span>
              </div>
              <CoarTag :variant="effectiveSourceVariant(g)" size="s">
                {{ effectiveSourceLabel(g) }}
              </CoarTag>
              <CoarTag v-if="g.Source === 'AutoMatched' && g.MaterializedMatches === false"
                       variant="warning" size="s">
                {{ t('admin.userDetails.effectiveGroups.driftBadge', {}, 'Drift — neu berechnen?') }}
              </CoarTag>
            </div>
          </div>
          <CoarNote v-if="effectiveDiagnostics.length > 0" variant="warning" class="mt-2">
            <div class="text-xs font-semibold mb-1">
              {{ t('admin.userDetails.effectiveGroups.diagnosticsHeading', {}, 'Skripte mit Fehlern') }}
            </div>
            <ul class="text-xs list-disc pl-4">
              <li v-for="d in effectiveDiagnostics" :key="d.GroupId">
                {{ t('admin.userDetails.effectiveGroups.diagnosticLine',
                     { group: d.GroupName, error: d.Error },
                     `Skript für Gruppe ${d.GroupName} konnte nicht ausgewertet werden: ${d.Error}`) }}
              </li>
            </ul>
          </CoarNote>
        </section>

        <section v-if="inheritedGroups.length > 0" class="flex-section">
          <div class="section-heading">
            {{ t('admin.userDetails.inheritedGroups', {}, 'Geerbt über genestete Gruppen') }}
          </div>
          <p class="tab-hint">
            {{ t('admin.userDetails.inheritedGroups.hint', {}, 'Diese Gruppen sind nicht direkt zugewiesen, aber der User ist über eine andere Gruppe (die diese als Mitglied hat) drin.') }}
          </p>
          <CoarListbox
            :options="inheritedOptions"
            searchable
            display-only
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :search-placeholder="t('admin.userDetails.searchGroups', {}, 'Search groups…')"
            height="280px"
          />
        </section>

        <p v-if="effectiveGroups.length === 0 && effectiveDiagnostics.length === 0 && inheritedGroups.length === 0"
           class="text-sm text-gray-500">
          {{ t('admin.userDetails.effectiveGroups.empty', {}, 'Dieser User ist aktuell in keiner Gruppe — weder direkt noch geerbt noch über Auto-Skripte.') }}
        </p>
      </div>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
.claim-row {
  display: grid;
  grid-template-columns: 7rem 1fr;
  gap: 8px;
  margin-bottom: 4px;
  align-items: start;
}
.claim-label {
  font-weight: 500;
  color: #525e76;
  text-transform: uppercase;
  letter-spacing: 0.02em;
  font-size: 0.7rem;
  padding-top: 2px;
}
.claim-values {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.claim-chip {
  display: inline-block;
  padding: 1px 6px;
  background: #e5e7eb;
  border-radius: 3px;
  font-size: 0.7rem;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
.claim-meta {
  margin-top: 6px;
  padding-top: 6px;
  border-top: 1px solid #e5e7eb;
  color: #6b7280;
  font-size: 0.7rem;
}
.section-heading {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  border-bottom: 1px solid #d1d5db;
  padding-bottom: 4px;
  margin-bottom: 8px;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 10px;
  border-radius: 9999px;
  font-size: 0.8rem;
  font-weight: 500;
  cursor: pointer;
  border: none;
  transition: opacity 0.15s;
}

.status-badge:hover { opacity: 0.8; }
.status-active { background-color: #dcfce7; color: #166534; }
.status-inactive { background-color: #f3f4f6; color: #6b7280; }
.status-exempt { background-color: #fef3c7; color: #92400e; }
.email-verify-status {
  margin-top: 6px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.email-verify-hint {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-left: 1.5rem;
}

.tab-bar {
  margin-bottom: 12px;
}

.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 2px 2px 16px;
  min-height: 0;
}

.tab-hint {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-top: -8px;
}

.flex-section {
  display: flex;
  flex-direction: column;
  min-height: 0;
  gap: 6px;
}

.effective-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: 4px;
  padding: 6px 8px;
  max-height: 220px;
  overflow-y: auto;
}

.effective-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 2px;
  font-size: 0.85rem;
}

.effective-icon {
  color: #6b7280;
  flex-shrink: 0;
}

.effective-name {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: baseline;
  gap: 6px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.effective-roles {
  color: #6b7280;
  font-size: 0.75rem;
}
</style>
