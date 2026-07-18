<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useGroupStore } from '@/stores/group.store'
import { useRoleStore } from '@/stores/role.store'
import { useUserStore } from '@/stores/user.store'
import { usePrincipalStore } from '@/stores/principal.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useClone, GROUP_CLONE } from '@/composables/useClone'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect, CoarMultiSelect, CoarTabGroup, CoarTab, CoarPopover, CoarCodeBlock, CoarIcon, CoarListbox, CoarDualListbox } from '@cocoar/vue-ui'
import type { CoarListboxOption } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { CoarScriptEditor } from '@cocoar/vue-script-editor'
import { membershipExamples, membershipPreamble } from './membershipScriptTypes'
import { useScriptTypes } from './useScriptTypes'
import type { GroupDto, MembershipMode, EmailMode } from '@/models/group'

const { sharedTypeDefinitions } = useScriptTypes()
const scriptExtraLibs = computed(() => [
  { content: sharedTypeDefinitions.value, filePath: 'file:///types/membership.d.ts' },
])

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const groupStore = useGroupStore()
const roleStore = useRoleStore()
const userStore = useUserStore()
const principalStore = usePrincipalStore()
const applicationsStore = useApplicationsStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create')
const initialLoad = ref(false)
const saving = ref(false)
const saveError = ref<string | null>(null)
const activeTab = ref<'general' | 'members' | 'script' | 'roles' | 'effective'>('general')
const effectiveMembers = ref<import('@/stores/group.store').EffectiveMembersDto | null>(null)
const effectiveLoading = ref(false)

async function loadEffectiveMembers() {
  if (isCreate.value) return
  effectiveLoading.value = true
  try {
    effectiveMembers.value = await groupStore.getEffectiveMembers(props.id)
  } finally {
    effectiveLoading.value = false
  }
}

watch(activeTab, (tab) => {
  if (tab === 'effective' && !effectiveMembers.value) {
    loadEffectiveMembers()
  }
})

const form = ref({
  Name: '',
  Description: '',
  MemberIds: [] as string[],
  RoleIds: [] as string[],
  MembershipMode: 'Manual' as MembershipMode,
  MembershipScript: '',
  MembershipLastError: null as string | null,
  // Federation v1: only meaningful for Auto groups — opts the group into
  // login-time externally-derived membership (session-scoped).
  ExternallyDrivable: false,
  Email: '' as string | undefined,
  EmailMode: 'Shared' as EmailMode,
  // App slugs the group is active in. The synthetic "*" entry means
  // "active in every app" (typical for the realm-admin group).
  BoundTo: [] as string[],
})

// "*" wildcard is a synthetic option — not a real app slug, but a valid
// BoundTo value the backend recognises. Listed first so it's discoverable.
const ALL_APPS_WILDCARD = '*'

const boundToOptions = computed(() => {
  const apps = applicationsStore.apps.map((a) => ({
    value: a.Slug,
    label: `${a.DisplayName} (${a.Slug})`,
  }))
  return [
    { value: ALL_APPS_WILDCARD, label: t('admin.groupDetails.boundTo.wildcardOption', {}, '★ All apps (*) — realm-wide') },
    ...apps,
  ]
})

const isAllAppsWildcard = computed(() => form.value.BoundTo.includes(ALL_APPS_WILDCARD))
const isDormantBoundTo = computed(() => form.value.BoundTo.length === 0)

const modalTitle = computed(() => {
  const name = form.value.Name?.trim()
  if (name) return name
  return isCreate.value ? t('admin.groupDetails.createTitle', {}, 'Create Group') : ''
})

const isAutoMode = computed(() => form.value.MembershipMode === 'Auto')

// Federation v1: a group whose selected roles confer realm:admin can NEVER be
// externally drivable (realm:admin is hard local-only). Mirror the backend
// GroupMembershipGuards check so the toggle disables before the API rejects.
const hasRealmAdminRole = computed(() =>
  roleStore.roles.some(r => form.value.RoleIds.includes(r.Id) && r.IsRealmAdmin))

// ExternallyDrivable only has effect on Auto groups (the deriver evaluates only
// Auto + drivable). Disable the toggle otherwise, and when a realm-admin role is
// selected (the guarded case).
const externallyDrivableDisabled = computed(() => !isAutoMode.value || hasRealmAdminRole.value)

// Keep the persisted flag honest: if the group leaves Auto mode or gains a
// realm-admin role, clear the toggle so a stale true can't linger.
watch(externallyDrivableDisabled, (disabled) => {
  if (disabled) form.value.ExternallyDrivable = false
})

const membershipModeOptions = computed(() => [
  { value: 'Manual', label: t('admin.groupDetails.membership.manual', {}, 'Manual') },
  { value: 'Auto', label: t('admin.groupDetails.membership.auto', {}, 'Automatic (script)') },
])

// EmailMode as a named two-option select (Shared = the group has its own shared
// mailbox; ExpandToMembers = notifications fan out to each member). Both states
// are visibly named — no cryptic bare checkbox.
const emailModeOptions = computed(() => [
  { value: 'Shared', label: t('admin.groupDetails.emailMode.shared', {}, 'Shared address') },
  { value: 'ExpandToMembers', label: t('admin.groupDetails.emailMode.expand', {}, 'Send to each member') },
])

// Script tab disappears in Manual mode — if it was active, fall back to Members
// so the user doesn't land on a hidden tab.
watch(isAutoMode, (auto) => {
  if (!auto && activeTab.value === 'script') activeTab.value = 'members'
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim()
    || saving.value
    || (isAutoMode.value && !form.value.MembershipScript.trim()),
  loading: saving.value,
  onClick: save,
}))

// Member picker now accepts persons AND other groups (nested groups).
// Exclude the current group itself to prevent trivial self-cycles at the UI level.
const memberOptions = computed<CoarListboxOption<string>[]>(() =>
  principalStore.lookupEntities
    .filter(p => p.Id !== props.id)
    .map(p => {
      if (p.Type === 'group') {
        return {
          value: p.Id,
          label: p.Label || p.Id,
          subtitle: p.Description ?? undefined,
          tooltip: p.Description ?? undefined,
          icon: 'users',
          group: t('admin.groupDetails.groupsLabel', {}, 'Groups'),
        }
      }
      if (p.Type === 'service-account') {
        return {
          value: p.Id,
          label: p.Label || p.Id,
          subtitle: p.Description ?? undefined,
          tooltip: p.Description ?? undefined,
          icon: 'cpu',
          group: t('admin.groupDetails.serviceAccountsLabel', {}, 'Service accounts'),
        }
      }
      // Person
      const fullName = [p.Firstname, p.Lastname].filter(Boolean).join(' ')
      const subtitleParts = [p.Acronym, fullName].filter(Boolean)
      return {
        value: p.Id,
        label: p.UserName || p.Label || p.Id,
        subtitle: subtitleParts.length > 0 ? subtitleParts.join(' | ') : undefined,
        icon: 'circle-user',
        group: t('admin.groupDetails.usersLabel', {}, 'Users'),
      }
    })
)

// Read-only view of the current members as rich list options — same shape as
// the picker items so the Script-tab listing looks identical.
const computedMemberOptions = computed<CoarListboxOption<string>[]>(() => {
  const byId = new Map(memberOptions.value.map(o => [o.value, o]))
  return form.value.MemberIds
    .map(id => byId.get(id))
    .filter((o): o is CoarListboxOption<string> => !!o)
})

function mapEffectiveMember(m: import('@/stores/group.store').EffectiveMemberDto, includeVia: boolean): CoarListboxOption<string> {
  const baseGroup = m.Type === 'group'
    ? t('admin.groupDetails.groupsLabel', {}, 'Groups')
    : m.Type === 'service-account'
    ? t('admin.groupDetails.serviceAccountsLabel', {}, 'Service accounts')
    : t('admin.groupDetails.usersLabel', {}, 'Users')
  const via = includeVia && m.ViaName ? m.ViaName : null
  const viaLabel = t('admin.groupDetails.via', {}, 'via')
  if (m.Type === 'group') {
    const desc = m.Description ?? ''
    const sub = via ? (desc ? `${desc} · ${viaLabel}: ${via}` : `${viaLabel}: ${via}`) : desc
    return {
      value: m.Id,
      label: m.Label,
      subtitle: sub || undefined,
      tooltip: m.Description || undefined,
      icon: 'users',
      group: baseGroup,
    }
  }
  if (m.Type === 'service-account') {
    const desc = m.Description ?? ''
    const sub = via ? (desc ? `${desc} · ${viaLabel}: ${via}` : `${viaLabel}: ${via}`) : desc
    return {
      value: m.Id,
      label: m.Label,
      subtitle: sub || undefined,
      tooltip: m.Description || undefined,
      icon: 'cpu',
      group: baseGroup,
    }
  }
  const fullName = [m.Firstname, m.Lastname].filter(Boolean).join(' ')
  const directPart = [m.Acronym, fullName].filter(Boolean).join(' | ')
  const sub = via ? (directPart ? `${directPart} · ${viaLabel}: ${via}` : `${viaLabel}: ${via}`) : directPart
  return {
    value: m.Id,
    label: m.UserName || m.Label,
    subtitle: sub || undefined,
    icon: 'circle-user',
    group: baseGroup,
  }
}

const effectiveDirectOptions = computed<CoarListboxOption<string>[]>(() =>
  effectiveMembers.value
    ? effectiveMembers.value.Direct.map(m => mapEffectiveMember(m, false))
    : [],
)

const effectiveNestedOptions = computed<CoarListboxOption<string>[]>(() =>
  effectiveMembers.value
    ? effectiveMembers.value.Nested.map(m => mapEffectiveMember(m, true))
    : [],
)

// Members listbox: Persons on top, Groups below — overrides the default alpha sort.
const memberGroupOrder = computed(() => [
  t('admin.groupDetails.usersLabel', {}, 'Users'),
  t('admin.groupDetails.groupsLabel', {}, 'Groups'),
])
const memberGroupSort = (a: string, b: string) => {
  const order = memberGroupOrder.value
  const ia = order.indexOf(a), ib = order.indexOf(b)
  return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib)
}

const roleOptions = computed<CoarListboxOption<string>[]>(() =>
  roleStore.roles.map(r => {
    const subtitle = r.IsRealmAdmin
      ? 'Realm Admin'
      : (r.Description || (r.PermissionIds.length === 0 ? 'No grants' : `${r.PermissionIds.length} permission(s)`))
    return {
      value: r.Id,
      label: r.Name,
      subtitle,
      tooltip: r.Description || undefined,
      icon: 'shield',
      group: r.IsRealmAdmin ? 'realm-admin' : 'app-scoped',
    }
  })
)

onMounted(async () => {
  initialLoad.value = true
  try {
    await Promise.all([
      roleStore.initialize(),
      userStore.loadLookup(),
      principalStore.loadLookup(),
      // BoundTo MultiSelect needs the App list for its options.
      applicationsStore.initialize(),
    ])

    if (isCreate.value) {
      // Clone: prefill from the staged source with the Name blanked. Members,
      // roles, script and BoundTo clone 1:1; the source's last script error is
      // not carried over (the clone hasn't run yet).
      const clone = consume<GroupDto>(GROUP_CLONE.entity)
      if (clone) {
        form.value = {
          Name: clone.Name ?? '',
          Description: clone.Description || '',
          MemberIds: [...(clone.MemberIds ?? [])],
          RoleIds: [...(clone.RoleIds ?? [])],
          MembershipMode: clone.MembershipMode || 'Manual',
          MembershipScript: clone.MembershipScript || '',
          MembershipLastError: null,
          ExternallyDrivable: clone.ExternallyDrivable ?? false,
          Email: clone.Email || '',
          EmailMode: clone.EmailMode || 'Shared',
          BoundTo: [...(clone.BoundTo ?? [])],
        }
      }
    } else {
      await groupStore.initialize()
      const group = groupStore.groups.find(g => g.Id === props.id)
      if (group) {
        form.value = {
          Name: group.Name,
          Description: group.Description || '',
          MemberIds: [...group.MemberIds],
          RoleIds: [...group.RoleIds],
          MembershipMode: group.MembershipMode || 'Manual',
          MembershipScript: group.MembershipScript || '',
          MembershipLastError: group.MembershipLastError ?? null,
          ExternallyDrivable: group.ExternallyDrivable ?? false,
          Email: group.Email || '',
          EmailMode: group.EmailMode || 'Shared',
          BoundTo: [...(group.BoundTo ?? [])],
        }
      }
    }
  } finally {
    initialLoad.value = false
  }
})

async function save() {
  if (!form.value.Name.trim()) return
  if (isAutoMode.value && !form.value.MembershipScript.trim()) return
  saving.value = true
  saveError.value = null
  try {
    const dto = {
      Name: form.value.Name,
      Description: form.value.Description || undefined,
      MemberIds: isAutoMode.value ? [] : form.value.MemberIds,
      RoleIds: form.value.RoleIds,
      MembershipMode: form.value.MembershipMode,
      MembershipScript: isAutoMode.value ? form.value.MembershipScript : undefined,
      // Only an Auto group can be externally driven; never send true for Manual
      // (the deriver ignores non-Auto groups anyway, but keep the payload honest).
      ExternallyDrivable: isAutoMode.value && form.value.ExternallyDrivable && !hasRealmAdminRole.value,
      Email: form.value.Email?.trim() || undefined,
      EmailMode: form.value.EmailMode,
      BoundTo: [...form.value.BoundTo],
    }
    const saved = isCreate.value
      ? await groupStore.createGroup(dto)
      : await groupStore.updateGroup(props.id, dto)

    // Keep the modal open and surface the script error so the user can fix it
    // without re-opening. Clearing happens automatically on the next successful save.
    if (saved?.MembershipLastError) {
      form.value.MembershipLastError = saved.MembershipLastError
      return
    }
    props.close()
  } catch (e: any) {
    // Surface server-side validation errors (TS transpile failures, name-taken, …)
    // instead of silently logging them. Supports several response shapes:
    //   { Errors: [{ Code, Description }] }  (GroupEndpoints)
    //   { error: "..." }                      (ErrorOrExtensions default)
    //   { detail / title: "..." }             (ASP.NET ProblemDetails)
    const body = e?.body
    let detail: string | null = null
    if (typeof body === 'object' && body !== null) {
      if (Array.isArray(body.Errors) && body.Errors.length > 0) {
        detail = body.Errors.map((err: any) => err?.Description ?? err?.description ?? '').filter(Boolean).join('\n')
      } else {
        detail = body.error ?? body.detail ?? body.title ?? null
      }
    } else if (typeof body === 'string') {
      detail = body
    }
    saveError.value = detail ?? e?.message ?? 'Save failed'
    console.error('Group save failed', e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="users" :footer-button="footerButton">
    <div v-if="!initialLoad" class="flex flex-col min-w-0 min-h-0 flex-1">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.groupDetails.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab id="members">{{ t('admin.groupDetails.tabs.members', {}, 'Members') }}</CoarTab>
        <CoarTab v-if="isAutoMode" id="script">{{ t('admin.groupDetails.tabs.script', {}, 'Script') }}</CoarTab>
        <CoarTab id="roles">{{ t('admin.groupDetails.tabs.roles', {}, 'Roles') }}</CoarTab>
        <CoarTab v-if="!isCreate" id="effective">{{ t('admin.groupDetails.tabs.effective', {}, 'Effective') }}</CoarTab>
      </CoarTabGroup>

      <div v-if="saveError" class="save-error">
        <div class="save-error-title">{{ t('admin.groupDetails.saveError', {}, 'Save failed') }}</div>
        <pre class="save-error-message">{{ saveError }}</pre>
        <button type="button" class="save-error-dismiss" @click="saveError = null" :aria-label="t('common.close', {}, 'Close')">×</button>
      </div>

      <!-- Tab: General -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <div class="modal-form">
          <!-- Section: Identity -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.groupDetails.section.identity', {}, 'Identity') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.groupDetails.name', {}, 'Name')" required>
                <CoarTextInput v-model="form.Name" clearable />
                <p class="field-hint">{{ t('admin.groupDetails.name.hint', {}, 'Display name of the group; also sets this dialog\'s title.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.groupDetails.type', {}, 'Type')">
                <CoarSelect v-model="form.MembershipMode" :options="membershipModeOptions" />
                <p class="field-hint">
                  {{ isAutoMode
                    ? t('admin.groupDetails.membership.autoHint', {}, 'Members are computed from the script in the Script tab.')
                    : t('admin.groupDetails.membership.manualHint', {}, 'Pick members directly in the Members tab.') }}
                </p>
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('admin.groupDetails.description', {}, 'Description')">
                <CoarTextInput v-model="form.Description" clearable :rows="2" />
                <p class="field-hint">{{ t('admin.groupDetails.description.hint', {}, 'Optional note; shown as a subtitle in member and picker lists.') }}</p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Notifications -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.groupDetails.section.notifications', {}, 'Notifications') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.groupDetails.emailModeLabel', {}, 'Email mode')">
                <CoarSelect v-model="form.EmailMode" :options="emailModeOptions" />
                <p class="field-hint">
                  {{ form.EmailMode === 'Shared'
                    ? t('admin.groupDetails.emailMode.sharedHint', {}, 'The group has one shared mailbox — notifications go to the address on the right.')
                    : t('admin.groupDetails.emailMode.expandHelp', {}, 'Notifications are sent to each member individually (recursive across nested groups).') }}
                </p>
              </CoarFormField>
              <CoarFormField v-if="form.EmailMode === 'Shared'" class="col-half" :label="t('admin.groupDetails.email', {}, 'Email address')">
                <CoarTextInput v-model="form.Email" clearable placeholder="team@example.com" />
                <p class="field-hint">{{ t('admin.groupDetails.emailMode.sharedHelp', {}, 'Notifications go to this address.') }}</p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Scope -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.groupDetails.section.scope', {}, 'Scope') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.groupDetails.boundTo', {}, 'Active in applications')">
                <CoarMultiSelect
                  v-model="form.BoundTo"
                  :options="boundToOptions"
                  searchable
                  clearable
                  :placeholder="t('admin.groupDetails.boundTo.placeholder', {}, 'Select applications…')" />
                <p class="field-hint">
                  <template v-if="isAllAppsWildcard">
                    {{ t('admin.groupDetails.boundTo.wildcardHint', {}, '★ "All applications" selected — this group is active in every application in the realm. Typical for the realm-admin group.') }}
                  </template>
                  <template v-else-if="isDormantBoundTo">
                    {{ t('admin.groupDetails.boundTo.dormantHint', {}, 'No applications selected — the group is dormant for permissions (e.g. a distribution list / org group). It still receives mail and appears in member views, but its roles grant nothing.') }}
                  </template>
                  <template v-else>
                    {{ t('admin.groupDetails.boundTo.scopedHint', {}, 'Only contributes to permission resolution when the requesting application is selected here. Its roles only fire in those applications.') }}
                  </template>
                </p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Advanced — only relevant for Automatic groups, so hidden
               entirely in the default Manual flow (no dangling cryptic toggle). -->
          <section v-if="isAutoMode" class="form-section">
            <h3 class="form-section-heading">{{ t('admin.groupDetails.section.advanced', {}, 'Advanced') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.groupDetails.externallyDrivable.label', {}, 'External membership (federation)')">
                <CoarCheckbox v-model="form.ExternallyDrivable" :disabled="externallyDrivableDisabled"
                  :label="t('admin.groupDetails.externallyDrivable.toggle', {}, 'Assign this group via a federated login script')" />
                <p class="field-hint">
                  <template v-if="hasRealmAdminRole">
                    {{ t('admin.groupDetails.externallyDrivable.realmAdminBlocked', {}, 'Disabled: this group confers realm:admin, which can never be externally driven (realm:admin is hard local-only). Remove the realm-admin role to enable.') }}
                  </template>
                  <template v-else>
                    {{ t('admin.groupDetails.externallyDrivable.hint', {}, 'When on, a trusted federated login whose membership script matches confers this group for that session only — never stored as a durable member, never realm:admin.') }}
                  </template>
                </p>
              </CoarFormField>
            </div>
          </section>
        </div>
      </div>

      <!-- Tab: Members -->
      <div v-show="activeTab === 'members'" class="tab-content">
        <section class="flex-section">
          <CoarDualListbox
            v-if="!isAutoMode"
            class="flex-1 min-h-0"
            v-model="form.MemberIds"
            :options="memberOptions"
            drag-drop
            :sort-groups="memberGroupSort"
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.groupDetails.availableMembers', {}, 'Available')"
            :selected-label="t('admin.groupDetails.selectedMembers', {}, 'Members')"
            :search-placeholder="t('admin.groupDetails.searchMembers', {}, 'Search people & groups…')"
          />
          <template v-else>
            <p class="script-help">
              {{ t('admin.groupDetails.membership.autoHint', {}, 'Members are computed from the script in the Script tab.') }}
            </p>
            <CoarListbox
              class="flex-1 min-h-0"
              :options="computedMemberOptions"
              :sort-groups="memberGroupSort"
              sort-options="asc"
              :label="t('admin.groupDetails.membership.currentMembers', {}, 'Current members (computed)')"
              searchable
              display-only
              :search-fields="['label', 'subtitle', 'group']"
              :search-placeholder="t('admin.groupDetails.searchMembers', {}, 'Search people & groups…')"
              :empty-text="t('admin.groupDetails.membership.noMembersYet', {}, 'No members yet — will be computed after save.')"
            />
          </template>
        </section>
      </div>

      <!-- Tab: Effective (resolved members) -->
      <div v-show="activeTab === 'effective'" class="tab-content">
        <div v-if="effectiveLoading" class="empty-hint">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <template v-else>
          <section class="flex-section">
            <div class="section-heading">{{ t('admin.groupDetails.effective.direct', {}, 'Direct members') }}</div>
            <CoarListbox
              class="flex-1 min-h-0"
              :options="effectiveDirectOptions"
              :sort-groups="memberGroupSort"
              sort-options="asc"
              searchable
              display-only
              :search-fields="['label', 'subtitle', 'group']"
              :search-placeholder="t('admin.groupDetails.searchMembers', {}, 'Search people & groups…')"
              :empty-text="t('admin.groupDetails.effective.noneDirect', {}, 'No direct members.')"
            />
          </section>
          <section v-if="effectiveNestedOptions.length > 0" class="flex-section">
            <div class="section-heading">{{ t('admin.groupDetails.effective.nested', {}, 'Via nested groups') }}</div>
            <CoarListbox
              class="flex-1 min-h-0"
              :options="effectiveNestedOptions"
              :sort-groups="memberGroupSort"
              sort-options="asc"
              searchable
              display-only
              :search-fields="['label', 'subtitle', 'group']"
              :search-placeholder="t('admin.groupDetails.searchMembers', {}, 'Search people & groups…')"
              :empty-text="t('admin.groupDetails.effective.noneNested', {}, 'No nested members.')"
            />
          </section>
        </template>
      </div>

      <!-- Tab: Roles -->
      <div v-show="activeTab === 'roles'" class="tab-content">
        <section class="flex-section">
          <CoarDualListbox
            class="flex-1 min-h-0"
            v-model="form.RoleIds"
            :options="roleOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.groupDetails.availableRoles', {}, 'Available')"
            :selected-label="t('admin.groupDetails.selectedRoles', {}, 'Assigned')"
            :search-placeholder="t('admin.groupDetails.searchRoles', {}, 'Search roles…')"
          />
        </section>
      </div>

      <!-- Tab: Script (auto only) -->
      <div v-show="activeTab === 'script'" class="tab-content">
        <section class="flex-section">
          <div class="script-label-row">
            <p class="script-help flex-1">
              {{ t('admin.groupDetails.membership.autoHelp', {}, 'Write a TypeScript arrow function returning true for principals that should be members.') }}
            </p>
            <CoarPopover mode="click">
              <button type="button" class="info-btn" :aria-label="t('admin.groupDetails.examples', {}, 'Examples')">
                <CoarIcon name="info" size="s" />
              </button>
              <template #content>
                <div class="examples-popover">
                  <p class="examples-intro">
                    {{ t('admin.groupDetails.examplesIntro', {}, 'Empty = no restriction. Examples:') }}
                  </p>
                  <div v-for="(ex, i) in membershipExamples" :key="i" class="example">
                    <div class="example-desc">{{ ex.description }}</div>
                    <CoarCodeBlock
                      :code="ex.code"
                      language="typescript"
                      :collapsible="false"
                      :show-copy="true"
                    />
                  </div>
                </div>
              </template>
            </CoarPopover>
          </div>
          <CoarScriptEditor
            class="flex-1 min-h-0"
            v-model="form.MembershipScript"
            :extra-libs="scriptExtraLibs"
            :preamble="membershipPreamble"
            variant="inline"
            script-mode
            placeholder="(p) => Type.Is(p, 'person') && p.IsActive"
          />

          <div v-if="form.MembershipLastError" class="auto-error">
            <div class="auto-error-title">
              {{ t('admin.groupDetails.membership.lastError', {}, 'Script error at last evaluation') }}
            </div>
            <pre class="auto-error-message">{{ form.MembershipLastError }}</pre>
          </div>
        </section>
      </div>

    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
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

.access-policy-help,
.script-help {
  font-size: 0.75rem;
  color: #6b7280;
  margin: 0 0 8px 0;
}


.script-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.script-label {
  font-size: 0.8rem;
  font-weight: 600;
  color: #374151;
  text-transform: capitalize;
}

.script-label-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 4px;
}

.info-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: none;
  background: transparent;
  color: var(--coar-text-muted, #6b7280);
  border-radius: 3px;
  cursor: pointer;
}

.info-btn:hover {
  background: var(--coar-surface-hover, #f3f4f6);
  color: var(--coar-text, #1f2937);
}

.examples-popover {
  padding: 4px;
  max-width: 480px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.examples-intro {
  font-size: 0.8rem;
  color: var(--coar-text-muted, #6b7280);
  margin: 0;
}

.example {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.example-desc {
  font-size: 0.75rem;
  font-weight: 500;
  color: var(--coar-text, #374151);
}

.mode-toggle {
  display: flex;
  gap: 16px;
  margin-bottom: 8px;
}


.email-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}


.auto-error {
  margin-top: 8px;
  padding: 8px 10px;
  background: var(--coar-background-danger-subtle, #fef2f2);
  border: 1px solid var(--coar-border-danger, #fca5a5);
  border-radius: 4px;
}

.auto-error-title {
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--coar-text-danger, #b91c1c);
  margin-bottom: 4px;
}

.auto-error-message {
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
  color: var(--coar-text-danger, #991b1b);
  white-space: pre-wrap;
  word-break: break-word;
  margin: 0;
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

.flex-section {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  gap: 6px;
}

.empty-hint {
  padding: 32px 16px;
  text-align: center;
  font-size: 0.875rem;
  color: var(--coar-text-muted, #6b7280);
  font-style: italic;
}

.save-error {
  position: relative;
  padding: 8px 32px 8px 12px;
  margin-bottom: 8px;
  background: var(--coar-background-danger-subtle, #fef2f2);
  border: 1px solid var(--coar-border-danger, #fca5a5);
  border-radius: 4px;
}

.save-error-title {
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--coar-text-danger, #b91c1c);
  margin-bottom: 4px;
}

.save-error-message {
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
  color: var(--coar-text-danger, #991b1b);
  white-space: pre-wrap;
  word-break: break-word;
  margin: 0;
}

.save-error-dismiss {
  position: absolute;
  top: 4px;
  right: 6px;
  width: 22px;
  height: 22px;
  border: none;
  background: transparent;
  color: var(--coar-text-danger, #991b1b);
  font-size: 1.1rem;
  line-height: 1;
  cursor: pointer;
  border-radius: 3px;
}

.save-error-dismiss:hover {
  background: rgba(185, 28, 28, 0.1);
}

.write-warning {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  padding: 8px 12px;
  margin-bottom: 8px;
  background: var(--coar-background-warning-subtle, #fffbeb);
  border: 1px solid var(--coar-border-warning, #fcd34d);
  border-radius: 4px;
}

.write-warning-icon {
  flex-shrink: 0;
  color: var(--coar-text-warning, #b45309);
  margin-top: 2px;
}

.write-warning-body {
  flex: 1;
  min-width: 0;
}

.write-warning-title {
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--coar-text-warning, #b45309);
  margin-bottom: 2px;
}

.write-warning-message {
  font-size: 0.75rem;
  color: var(--coar-text, #374151);
  line-height: 1.4;
}
</style>
