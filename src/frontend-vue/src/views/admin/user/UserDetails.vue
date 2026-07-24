<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useUserStore, type UserGroupDto, type InheritedUserGroupDto, type EffectiveGroupDto, type EffectiveGroupDiagnostic } from '@/stores/user.store'
import { useGroupStore } from '@/stores/group.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { CoarTextInput, CoarNumberInput, CoarFormField, CoarIcon, CoarTabGroup, CoarTab, CoarListbox, CoarDualListbox, CoarButton, CoarCheckbox, CoarTag } from '@cocoar/vue-ui'
import type { CoarListboxOption } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import AppNote from '@/components/AppNote.vue'

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
const overrideInput = ref<number | null>(null)  // empty = no override / fall back to global default
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
  const value = overrideInput.value
  return value == null || !Number.isFinite(value) ? null : Math.max(0, Math.trunc(value))
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
  stagedGroupIds.value = data.Direct.map(g => g.Id)
  originalGroupIds.value = [...stagedGroupIds.value]
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
    return t('admin.userDetails.effectiveGroups.sourceDirect', {}, 'Direct')
  }
  if (g.Source === 'InheritedManual') {
    // Show the entry direct group (first hop in the Via chain) so the admin
    // sees how the inherited membership was reached.
    const via = g.Via?.[0]?.Name ?? ''
    return t('admin.userDetails.effectiveGroups.sourceInherited', { via }, `Inherited via ${via}`)
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

// Direct-group membership is STAGED into the form and committed by the single
// Save (Modal & Form Contract R2/R3) — no per-click writes. stagedGroupIds is
// the dual-listbox model; originalGroupIds is the baseline for the save-time diff.
const stagedGroupIds = ref<string[]>([])
const originalGroupIds = ref<string[]>([])

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

// Client-side email-format guard. Presence is handled separately (required);
// this only fires for a non-empty, malformed address so the admin gets an
// inline cue instead of silently persisting an unusable address (email drives
// forgot-password / magic-link). The server enforces the same rule.
const emailInvalid = computed(() => {
  const e = form.value.Email.trim()
  return e.length > 0 && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e)
})

// Configurable (App⊕realm) required-identity-field policy, resolved from
// /api/app-info (the admin SPA host → realm policy). Drives which inputs are
// shown / starred / required. Default = all Optional (today's behaviour).
const fieldPolicy = computed(() => appConfig.config.RegistrationFields)
const showUsername = computed(() => fieldPolicy.value.Username !== 'Off')
const usernameRequired = computed(() => fieldPolicy.value.Username === 'Required')
const firstnameRequired = computed(() => fieldPolicy.value.Firstname === 'Required')
const lastnameRequired = computed(() => fieldPolicy.value.Lastname === 'Required')

// A required field that is empty blocks save. On edit a blank username means
// "no change" (the user keeps its existing one), so it isn't enforced there;
// a cleared required NAME is a real empty and is blocked on both paths.
const requiredFieldMissing = computed(() => {
  const p = fieldPolicy.value
  const nameMissing =
    (p.Firstname === 'Required' && !form.value.Firstname.trim())
    || (p.Lastname === 'Required' && !form.value.Lastname.trim())
  if (isCreate.value)
    return nameMissing || (p.Username === 'Required' && !form.value.UserName.trim())
  return nameMissing
})

// Account state
const isActive = ref(true)
const originalActive = ref(true)

const modalTitle = computed(() => {
  const name = `${form.value.Firstname} ${form.value.Lastname}`.trim()
  const acronym = form.value.Acronym?.trim()
  if (name && acronym) return `${name} | ${acronym}`
  if (name) return name
  if (acronym) return acronym
  if (isCreate.value) return t('admin.userDetails.createTitle', {}, 'Create User')
  // Existing user without a display name — fall back to the login identity
  // (username, then email) so the modal header is never blank.
  return form.value.UserName?.trim() || form.value.Email?.trim() || ''
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  // Email is always required (the anchor). Username + first/last name follow the
  // configurable (App⊕realm) policy — required ones must be filled, defaulting to
  // the lenient "all Optional" behaviour when the realm never configured it.
  disabled: !form.value.Email.trim()
    || emailInvalid.value
    || requiredFieldMissing.value
    || loading.value
    || graceBusy.value,
  onClick: save,
}))


onMounted(async () => {
  // Ensure the field policy is available (idempotent; usually already loaded at boot).
  appConfig.load()
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
      overrideInput.value = sec.GracePeriodDaysOverride ?? null
      exemptLocal.value = sec.TwoFactorExempt
      originalOverride.value = sec.GracePeriodDaysOverride
      originalExempt.value = sec.TwoFactorExempt
    } finally {
      loading.value = false
    }
  }
})

async function save() {
  if (!form.value.Email.trim() || emailInvalid.value) return
  loading.value = true
  try {
    if (isCreate.value) {
      await userStore.createEntity({
        Firstname: form.value.Firstname,
        Lastname: form.value.Lastname,
        Acronym: form.value.Acronym || undefined,
        Email: form.value.Email || undefined,
        // Blank username is fine — the backend defaults it to the email address.
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
          UserName: form.value.UserName || existing.UserName,
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
        // Username left blank on edit = no change (a user must keep a username);
        // the email-default only applies on create.
        UserName: form.value.UserName || undefined,
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

      // Direct-group membership: commit the staged diff as part of the one Save.
      // Guarded on groupsLoaded so a user whose Groups tab was never opened does
      // not get every membership stripped by an empty staged list.
      if (groupsLoaded.value) {
        const added = stagedGroupIds.value.filter(id => !originalGroupIds.value.includes(id))
        const removed = originalGroupIds.value.filter(id => !stagedGroupIds.value.includes(id))
        for (const id of added) await userStore.addGroup(props.id, id)
        for (const id of removed) await userStore.removeGroup(props.id, id)
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
    <div v-if="!loading" class="flex flex-col min-w-0 min-h-0 flex-1"
      :class="{ 'user-edit-frame': !isCreate }">
      <CoarTabGroup v-if="!isCreate" v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.userDetails.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab id="groups">{{ t('admin.userDetails.tabs.groups', {}, 'Direct Groups') }}</CoarTab>
        <CoarTab id="effective">{{ t('admin.userDetails.tabs.effective', {}, 'Effektiv') }}</CoarTab>
        <CoarTab id="security">{{ t('admin.userDetails.tabs.security', {}, 'Security') }}</CoarTab>
      </CoarTabGroup>

      <!-- Tab: General -->
      <div v-show="isCreate || activeTab === 'general'" class="tab-content">
        <div class="modal-form">
          <!-- Section: Identity -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.userDetails.section.identity', {}, 'Identity') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.users.firstname', {}, 'First Name')" :required="firstnameRequired">
                <CoarTextInput v-model="form.Firstname" clearable />
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.users.lastname', {}, 'Last Name')" :required="lastnameRequired">
                <CoarTextInput v-model="form.Lastname" clearable />
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.users.acronym', {}, 'Acronym')"
                :hint="t('admin.userDetails.acronym.hint', {}, 'Initials; appear in the title as &quot;Name | Acronym&quot;. Optional.')">
                <CoarTextInput v-model="form.Acronym" clearable />
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Sign-in -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.userDetails.section.signin', {}, 'Anmeldung') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.users.email', {}, 'Email')" required
                :hint="t('admin.userDetails.email.hint', {}, 'Primary address; needed for password reset and magic link.')">
                <CoarTextInput v-model="form.Email" clearable />
                <span v-if="emailInvalid" class="text-xs text-red-600">{{ t('admin.userDetails.emailInvalid', {}, 'Please enter a valid email address.') }}</span>
              </CoarFormField>
              <CoarFormField v-if="showUsername" class="col-half" :label="t('admin.users.username', {}, 'Username')" :required="usernameRequired"
                :hint="usernameRequired
                  ? t('admin.userDetails.username.hintRequired', {}, 'Login name; must be unique and is required.')
                  : t('admin.userDetails.username.hint', {}, 'Login name; must be unique. Empty = the email address is used.')">
                <CoarTextInput v-model="form.UserName" clearable />
                <span v-if="userNameError" class="text-xs text-red-600">{{ userNameError }}</span>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Account status — edit only. Active flag + email-verified
               override, the account-state toggles set off in their own band. -->
          <section v-if="!isCreate" class="form-section">
            <h3 class="form-section-heading">{{ t('admin.userDetails.section.accountStatus', {}, 'Kontostatus') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.userDetails.activeLabel', {}, 'Konto')"
                :hint="t('admin.userDetails.activeHint', {}, 'Disabled users can\'t sign in.')">
                <CoarCheckbox v-model="isActive"
                  :label="t('admin.userDetails.activeCheckbox', {}, 'User active')" />
              </CoarFormField>
              <!-- Email-verified toggle pinned at the section end with its existing
                   v-if (only meaningful once an email is set). -->
              <CoarFormField v-if="form.Email" class="col-full" :label="t('admin.userDetails.emailVerifiedLabel', {}, 'Email Status')"
                :hint="emailConfirmed
                    ? t('admin.userDetails.emailVerifiedHint', {}, 'Forgot-password and self-magic-link are unlocked for this user.')
                    : t('admin.userDetails.emailUnverifiedHint', {}, 'Forgot-password and self-magic-link are blocked until the user verifies their email.')">
                <CoarCheckbox v-model="emailConfirmed"
                  :label="t('admin.userDetails.emailVerifiedToggle', {}, 'Mark email address as verified')" />
              </CoarFormField>
            </div>
          </section>

          <!-- On create the email-verified override still applies (the admin vouches
               for the address they're typing). Kept at the end, mirroring edit. -->
          <section v-if="isCreate && form.Email" class="form-section">
            <h3 class="form-section-heading">{{ t('admin.userDetails.section.accountStatus', {}, 'Kontostatus') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.userDetails.emailVerifiedLabel', {}, 'Email Status')"
                :hint="emailConfirmed
                    ? t('admin.userDetails.emailVerifiedHint', {}, 'Forgot-password and self-magic-link are unlocked for this user.')
                    : t('admin.userDetails.emailUnverifiedHint', {}, 'Forgot-password and self-magic-link are blocked until the user verifies their email.')">
                <CoarCheckbox v-model="emailConfirmed"
                  :label="t('admin.userDetails.emailVerifiedToggle', {}, 'Mark email address as verified')" />
              </CoarFormField>
            </div>
          </section>
        </div>
      </div>

      <!-- Tab: Security -->
      <div v-show="!isCreate && activeTab === 'security'" class="tab-content">
        <section v-if="securityInfo" class="flex flex-col gap-4 text-sm">
          <!-- 2FA status -->
          <div>
            <div class="section-heading">{{ t('admin.userDetails.twoFactorHeading', {}, 'Two-factor authentication') }}</div>
            <div class="flex items-center gap-2">
              <span class="text-gray-600">{{ t('admin.userDetails.twoFactor', {}, '2FA:') }}</span>
              <CoarTag v-if="securityInfo.Has2FA" variant="success" size="s">
                <CoarIcon name="check" size="s" />
                {{ securityInfo.TwoFactorMethods.join(', ') }}
              </CoarTag>
              <CoarTag v-else-if="exemptLocal" variant="warning" size="s">
                <CoarIcon name="shield-alert" size="s" />
                {{ t('admin.userDetails.exemptBadge', {}, 'Exempt — 2FA not required') }}
              </CoarTag>
              <CoarTag v-else variant="neutral" size="s">
                <CoarIcon name="x" size="s" />
                {{ t('admin.userDetails.noTwoFactor', {}, 'Not configured') }}
              </CoarTag>
            </div>
          </div>

          <!-- Grace period — hidden when user has 2FA or is exempt -->
          <div v-if="!securityInfo.Has2FA && !exemptLocal && appConfig.config.AuthenticationMinimumLevel >= 1">
            <div class="section-heading">{{ t('admin.userDetails.graceHeading', {}, 'Grace period') }}</div>
            <AppNote v-if="graceDaysRemaining === null" variant="info" :truncate="false">
              {{ t('admin.userDetails.graceNotStarted', {}, 'Grace period starts on first login.') }}
            </AppNote>
            <AppNote v-else-if="graceDaysRemaining > 0" variant="warning" :truncate="false">
              {{ t('admin.userDetails.graceRemaining', { days: graceDaysRemaining }, `${graceDaysRemaining} day(s) remaining.`) }}
            </AppNote>
            <AppNote v-else variant="error" :truncate="false">
              {{ t('admin.userDetails.graceExpired', {}, 'Grace expired — next login forces 2FA setup.') }}
            </AppNote>
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
              <CoarFormField :label="t('admin.userDetails.policyDays', {}, 'Individual deadline in days (empty = global default)')">
                <CoarNumberInput v-model="overrideInput" :min="0"
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
        <!-- In edit mode the body has a fixed height (.user-edit-frame, so the
             modal doesn't resize on tab switch); this section fills it via flex so
             the dual-listbox gets a definite height. -->
        <section class="flex-section groups-editor">
          <CoarDualListbox
            v-model="stagedGroupIds"
            :options="allGroupsOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.userDetails.availableGroups', {}, 'Available')"
            :selected-label="t('admin.userDetails.memberOf', {}, 'Member of')"
            :search-placeholder="t('admin.userDetails.searchGroups', {}, 'Search groups…')"
            :disabled="loading"
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
            {{ t('admin.userDetails.effectiveGroups.hint', {}, 'Materialized view of all groups currently assigned to this user — directly, inherited via nested groups, or via auto-script.') }}
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
                {{ t('admin.userDetails.effectiveGroups.driftBadge', {}, 'Drift — recalculate?') }}
              </CoarTag>
            </div>
          </div>
          <AppNote v-if="effectiveDiagnostics.length > 0" variant="warning" :truncate="false" class="mt-2">
            <div class="text-xs font-semibold mb-1">
              {{ t('admin.userDetails.effectiveGroups.diagnosticsHeading', {}, 'Skripte mit Fehlern') }}
            </div>
            <ul class="text-xs list-disc pl-4">
              <li v-for="d in effectiveDiagnostics" :key="d.GroupId">
                {{ t('admin.userDetails.effectiveGroups.diagnosticLine',
                     { group: d.GroupName, error: d.Error },
                     `Script for group ${d.GroupName} could not be evaluated: ${d.Error}`) }}
              </li>
            </ul>
          </AppNote>
        </section>

        <section v-if="inheritedGroups.length > 0" class="flex-section">
          <div class="section-heading">
            {{ t('admin.userDetails.inheritedGroups', {}, 'Inherited via nested groups') }}
          </div>
          <p class="tab-hint">
            {{ t('admin.userDetails.inheritedGroups.hint', {}, 'These groups aren\'t assigned directly, but the user is in them via another group (which has them as a member).') }}
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
          {{ t('admin.userDetails.effectiveGroups.empty', {}, 'This user currently isn\'t in any group — neither directly, nor inherited, nor via auto-scripts.') }}
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

/* The Groups dual-listbox tab gets an explicit height so it stays usable
   inside the cap-to-content modal (which has no definite height to inherit). */
/* Edit mode pins a fixed body height so the modal keeps one size across all tabs
   (no resize on tab switch). Create has no tabs and stays cap-to-content/compact.
   flex:0 0 auto is required so this height wins over the root div's `flex-1`
   (Tailwind flex-1 sets flex-basis:0%, which would otherwise ignore the height). */
.user-edit-frame {
  flex: 0 0 auto;
  height: 60vh;
}
/* The Groups dual-listbox section fills that fixed body height. */
.groups-editor {
  flex: 1;
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
