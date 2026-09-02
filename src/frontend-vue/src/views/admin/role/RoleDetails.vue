<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoleStore } from '@/stores/role.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useClone, ROLE_CLONE } from '@/composables/useClone'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarSelect,
  CoarTabGroup,
  CoarTab,
  CoarDualListbox,
  CoarDivider,
  CoarIcon,
  CoarPopover,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import type { RoleDto } from '@/models/role'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const roleStore = useRoleStore()
const applicationsStore = useApplicationsStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create')

// ── ADR-0005 staging: role saves commit onto the active draft. Role names are
// editable, so the staged Key is pinned to the ORIGINAL name — a rename then
// replaces the staged entry instead of cloning it.
const staging = useDraftStaging('roles')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value)
const stagedKey = ref<string | null>(null)

function fromStaged(e: ManifestEntity): FormState {
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const app = applicationsStore.apps.find((a) => a.Slug === str(e.App))
  const catalog = app?.Permissions ?? []
  const permissionIds: string[] = []
  for (const perm of Array.isArray(e.Permissions) ? (e.Permissions as ManifestEntity[]) : []) {
    const hit = catalog.find((c) => c.Resource === perm.Resource && c.Action === perm.Action)
    if (hit) permissionIds.push(hit.Id)
  }
  const isRealmAdmin = e.IsRealmAdmin === true
  return {
    Name: str(e.Name),
    Description: str(e.Description),
    AppId: isRealmAdmin ? '' : (app?.Id ?? ''),
    IsRealmAdmin: isRealmAdmin,
    PermissionIds: isRealmAdmin ? [] : permissionIds,
  }
}

function toStaged(): ManifestEntity {
  const entity: ManifestEntity = {
    Name: form.value.Name.trim(),
    IsRealmAdmin: form.value.IsRealmAdmin,
  }
  if (stagedKey.value) entity.Key = stagedKey.value
  // v2 merge-patch: explicit null stages the clear (absent would keep live).
  entity.Description = form.value.Description.trim() || null
  const app = form.value.IsRealmAdmin
    ? undefined
    : applicationsStore.apps.find((a) => a.Id === form.value.AppId)
  if (app) {
    entity.App = app.Slug
    entity.Permissions = (app.Permissions ?? [])
      .filter((c) => form.value.PermissionIds.includes(c.Id))
      .map((c) => ({ Resource: c.Resource, Action: c.Action }))
  }
  return entity
}

const loading = ref(false)
const saveError = ref('')
const activeTab = ref<'general' | 'permissions'>('general')
type RoleType = 'application' | 'realmAdmin'

interface FormState {
  Name: string
  Description: string
  /** Empty = no App link (pure realm-admin role). */
  AppId: string
  IsRealmAdmin: boolean
  /** Selected AppPermission.Ids in the linked App's catalog. */
  PermissionIds: string[]
}

const form = ref<FormState>({
  Name: '',
  Description: '',
  AppId: '',
  IsRealmAdmin: false,
  PermissionIds: [],
})

const roleTypeOptions = computed(() => [
  { value: 'application', label: t('admin.roleDetails.type.application', {}, 'Application role') },
  { value: 'realmAdmin', label: t('admin.roleDetails.type.realmAdmin', {}, 'Realm administrator') },
])

const roleType = computed<RoleType>({
  get: () => form.value.IsRealmAdmin ? 'realmAdmin' : 'application',
  set: (value) => onRoleTypeChange(value),
})

const appOptions = computed(() =>
  applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
)

/** Catalog of the currently linked App, sorted for stable display. */
const linkedAppCatalog = computed(() => {
  if (!form.value.AppId) return []
  const app = applicationsStore.apps.find((a) => a.Id === form.value.AppId)
  if (!app) return []
  return [...(app.Permissions ?? [])].sort((a, b) => {
    const lhs = `${a.Resource}:${a.Action}`
    const rhs = `${b.Resource}:${b.Action}`
    return lhs.localeCompare(rhs)
  })
})

const permissionOptions = computed(() =>
  linkedAppCatalog.value.map((permission) => ({
    value: permission.Id,
    label: `${permission.Resource}:${permission.Action}`,
    subtitle: permission.Description ?? undefined,
    group: permission.Resource,
  })),
)

const nameError = computed(() =>
  form.value.Name.trim()
    ? ''
    : t('admin.roleDetails.validation.nameRequired', {}, 'Name is required.'),
)

const appError = computed(() =>
  !form.value.IsRealmAdmin && !form.value.AppId
    ? t('admin.roleDetails.validation.appRequired', {}, 'Select an application for this role.')
    : '',
)

const generalIssues = computed(() => [nameError.value, appError.value].filter(Boolean))

const modalTitle = computed(() => {
  const name = form.value.Name?.trim()
  if (name) return name
  return isCreate.value ? t('admin.roleDetails.createTitle', {}, 'Create Role') : ''
})

const footerButton = computed(() => ({
  visible: true,
  text: stagedSave.value
    ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
    : isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: generalIssues.value.length > 0 || loading.value,
  onClick: save,
}))

onMounted(async () => {
  await applicationsStore.initialize()
  if (isDraftRow.value) {
    // Draft-created role: the staged manifest entity IS the state.
    const entity = staging.findStaged(staging.draftKeyOf(props.id))
    if (entity) {
      form.value = fromStaged(entity)
      stagedKey.value = (typeof entity.Key === 'string' && entity.Key) || staging.draftKeyOf(props.id)
    }
    return
  }
  if (isCreate.value) {
    // Clone: prefill from the staged source with the Name blanked. Realm-admin
    // roles are normalized to their deliberately App-less shape.
    const clone = consume<RoleDto>(ROLE_CLONE.entity)
    if (clone) {
      form.value = {
        Name: clone.Name ?? '',
        Description: clone.Description || '',
        AppId: clone.IsRealmAdmin ? '' : (clone.AppId ?? ''),
        IsRealmAdmin: clone.IsRealmAdmin,
        PermissionIds: clone.IsRealmAdmin ? [] : [...(clone.PermissionIds ?? [])],
      }
    }
    return
  }
  loading.value = true
  try {
    await roleStore.initialize()
    const role = roleStore.roles.find(r => r.Id === props.id)
    if (role) {
      form.value = {
        Name: role.Name,
        Description: role.Description || '',
        AppId: role.IsRealmAdmin ? '' : (role.AppId ?? ''),
        IsRealmAdmin: role.IsRealmAdmin,
        PermissionIds: role.IsRealmAdmin ? [] : [...(role.PermissionIds ?? [])],
      }
      // Staging overlay: show the STAGED role state when the draft carries it
      // (draft keys resolve Key ?? Name, so the live name finds a staged rename).
      stagedKey.value = role.Name
      if (stagedSave.value && staging.draftStore.current) {
        const entity = staging.findStaged(role.Name)
        if (entity) form.value = fromStaged(entity)
      }
    }
  } finally {
    loading.value = false
  }
})

function onAppIdChange() {
  // Detaching the App must clear the catalog subset — otherwise stale ids
  // would land in the payload and the backend would reject them.
  form.value.PermissionIds = []
}

function onRoleTypeChange(value: RoleType) {
  form.value.IsRealmAdmin = value === 'realmAdmin'
  form.value.AppId = ''
  form.value.PermissionIds = []
}

async function save() {
  if (generalIssues.value.length > 0) return
  loading.value = true
  saveError.value = ''
  try {
    // ADR-0005: commit onto the active draft instead of writing live.
    if (stagedSave.value) {
      await staging.stage(form.value.Name.trim(), toStaged())
      props.close()
      return
    }
    const dto = {
      Name: form.value.Name.trim(),
      Description: form.value.Description.trim() || null,
      AppId: form.value.IsRealmAdmin ? null : (form.value.AppId || null),
      IsRealmAdmin: form.value.IsRealmAdmin,
      PermissionIds: form.value.IsRealmAdmin
        ? []
        : (form.value.AppId ? [...form.value.PermissionIds] : []),
    }
    if (isCreate.value) {
      await roleStore.createRole(dto)
    } else {
      await roleStore.updateRole(props.id, dto)
    }
    props.close()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    saveError.value = err?.data?.Message
      ?? err?.message
      ?? t('admin.roleDetails.saveError', {}, 'The role could not be saved.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="shield" :footer-button="footerButton">
    <div v-if="!loading" class="role-editor-frame">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">
          <span class="tab-label">
            {{ t('admin.roleDetails.tabs.general', {}, 'General') }}
            <CoarPopover
              v-if="generalIssues.length"
              class="tab-issue-popover"
              mode="hover"
              :offset="8">
              <span class="tab-issue" role="img" :aria-label="generalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.roleDetails.validation.incomplete', {}, 'Missing information') }}</h4>
                  <ul>
                    <li v-for="issue in generalIssues" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab id="permissions">{{ t('admin.roleDetails.tabs.permissions', {}, 'Permissions') }}</CoarTab>
      </CoarTabGroup>

      <div v-show="activeTab === 'general'" class="tab-content">
        <div class="modal-form">
          <section class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.roleDetails.section.identity', {}, 'Identity') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField
                class="col-full"
                :label="t('admin.roleDetails.name', {}, 'Name')"
                required
                :error="nameError"
                :hint="t('admin.roleDetails.name.hint', {}, 'Display/identification name of the role.')">
                <CoarTextInput v-model="form.Name" clearable />
              </CoarFormField>
              <CoarFormField
                class="col-full"
                :label="t('admin.roleDetails.description', {}, 'Description')"
                :hint="t('admin.roleDetails.description.hint', {}, 'Optional note describing what this role is for.')">
                <CoarTextInput v-model="form.Description" clearable />
              </CoarFormField>
            </div>
          </section>

          <section class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.roleDetails.section.scope', {}, 'Scope') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField
                class="col-half"
                :label="t('admin.roleDetails.type.label', {}, 'Role type')"
                required
                :hint="t('admin.roleDetails.type.hint', {}, 'Application roles grant selected permissions; realm administrators bypass permission checks in this realm.')">
                <CoarSelect v-model="roleType" :options="roleTypeOptions" />
              </CoarFormField>
              <CoarFormField
                class="col-half"
                :label="t('admin.roleDetails.app', {}, 'Application')"
                :required="!form.IsRealmAdmin"
                :error="appError"
                :hint="form.IsRealmAdmin
                  ? t('admin.roleDetails.app.realmAdminHint', {}, 'Realm administrators are deliberately not linked to an application.')
                  : t('admin.roleDetails.app.linkedHint', {}, 'The role grants selected permissions from this application.')">
                <CoarSelect
                  v-model="form.AppId"
                  :options="appOptions"
                  :disabled="form.IsRealmAdmin"
                  :placeholder="form.IsRealmAdmin
                    ? t('admin.roleDetails.app.realmAdminPlaceholder', {}, 'No application (realm administrator)')
                    : t('admin.roleDetails.app.placeholder', {}, 'Select application…')"
                  @update:model-value="onAppIdChange" />
              </CoarFormField>
              <CoarNotice v-if="form.IsRealmAdmin" class="col-full" variant="warning">
                {{ t('admin.roleDetails.isRealmAdmin.warning', {}, 'This role grants realm:admin and bypasses every permission check in this realm. Application and individual permissions are cleared.') }}
              </CoarNotice>
            </div>
          </section>
        </div>
        <CoarNotice v-if="saveError" variant="error">{{ saveError }}</CoarNotice>
      </div>

      <div v-show="activeTab === 'permissions'" class="tab-content">
        <CoarNotice v-if="form.IsRealmAdmin" variant="warning">
          {{ t('admin.roleDetails.permissions.realmAdmin', {}, 'A realm administrator bypasses every application permission in this realm; individual permissions cannot be assigned.') }}
        </CoarNotice>
        <CoarNotice v-else-if="!form.AppId" variant="warning">
          {{ t('admin.roleDetails.permissions.noApp', {}, 'Select an application in the General tab before assigning permissions.') }}
        </CoarNotice>
        <CoarNotice v-else-if="linkedAppCatalog.length === 0" variant="info">
          {{ t('admin.roleDetails.permissions.empty', {}, 'The selected application has no permission catalog entries.') }}
        </CoarNotice>
        <section v-else class="permissions-editor">
          <CoarDualListbox
            v-model="form.PermissionIds"
            class="flex-1 min-h-0"
            :options="permissionOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.roleDetails.permissions.available', {}, 'Available')"
            :selected-label="t('admin.roleDetails.permissions.selected', {}, 'Assigned')"
            :search-placeholder="t('admin.roleDetails.permissions.search', {}, 'Search permissions…')" />
        </section>
        <CoarNotice v-if="saveError" variant="error">{{ saveError }}</CoarNotice>
      </div>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
.role-editor-frame {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.tab-bar {
  flex-shrink: 0;
  margin-bottom: 12px;
}

.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
  overflow-y: auto;
}

.form-section + .form-section {
  margin-top: 1.5rem;
}

.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.permissions-editor {
  display: flex;
  flex: 1;
  min-height: 0;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.tab-issue {
  display: flex;
  align-items: center;
  color: var(--coar-text-warning-primary, #b45309);
  cursor: help;
}

.tab-issue-panel {
  width: min(24rem, 70vw);
  padding: 0.75rem 0.875rem;
}

.tab-issue-panel h4 {
  margin: 0 0 0.4rem;
  font-size: 0.875rem;
  font-weight: 600;
}

.tab-issue-panel ul {
  margin: 0;
  padding-left: 1rem;
  font-size: 0.8rem;
}
</style>
