<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoleStore } from '@/stores/role.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useClone, ROLE_CLONE } from '@/composables/useClone'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect, CoarNote, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
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
const loading = ref(false)
const activeTab = ref<'general' | 'permissions'>('general')

interface FormState {
  Name: string
  Description: string
  /** Empty = no App link (pure realm-admin role). */
  AppId: string
  IsRealmAdmin: boolean
  /** Selected AppPermission.Ids in the linked App's catalog. */
  PermissionIds: Set<string>
}

const form = ref<FormState>({
  Name: '',
  Description: '',
  AppId: '',
  IsRealmAdmin: false,
  PermissionIds: new Set<string>(),
})

const appOptions = computed(() => [
  { value: '', label: t('admin.roleDetails.app.none', {}, '— None (realm-admin role)') },
  ...applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
])

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

const modalTitle = computed(() => {
  const name = form.value.Name?.trim()
  if (name) return name
  return isCreate.value ? t('admin.roleDetails.createTitle', {}, 'Create Role') : ''
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || loading.value,
  onClick: save,
}))

onMounted(async () => {
  applicationsStore.initialize()
  if (isCreate.value) {
    // Clone: prefill from the staged source with the Name blanked. The App-link
    // + its catalog subset clone 1:1.
    const clone = consume<RoleDto>(ROLE_CLONE.entity)
    if (clone) {
      form.value = {
        Name: clone.Name ?? '',
        Description: clone.Description || '',
        AppId: clone.AppId ?? '',
        IsRealmAdmin: clone.IsRealmAdmin,
        PermissionIds: new Set(clone.PermissionIds ?? []),
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
        AppId: role.AppId ?? '',
        IsRealmAdmin: role.IsRealmAdmin,
        PermissionIds: new Set(role.PermissionIds ?? []),
      }
    }
  } finally {
    loading.value = false
  }
})

function togglePermissionId(id: string) {
  const next = new Set(form.value.PermissionIds)
  if (next.has(id)) next.delete(id); else next.add(id)
  form.value.PermissionIds = next
}

function onAppIdChange() {
  // Detaching the App must clear the catalog subset — otherwise stale ids
  // would land in the payload and the backend would reject them.
  form.value.PermissionIds = new Set<string>()
}

async function save() {
  if (!form.value.Name.trim()) return
  loading.value = true
  try {
    const dto = {
      Name: form.value.Name,
      Description: form.value.Description || null,
      AppId: form.value.AppId || null,
      IsRealmAdmin: form.value.IsRealmAdmin,
      PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
    }
    if (isCreate.value) {
      await roleStore.createRole(dto)
    } else {
      await roleStore.updateRole(props.id, dto)
    }
    props.close()
  } catch (e) {
    console.error('Role save failed', e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="shield" :footer-button="footerButton">
    <div v-if="!loading" class="flex flex-col min-w-0 min-h-0 flex-1">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.roleDetails.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab id="permissions">{{ t('admin.roleDetails.tabs.permissions', {}, 'Permissions') }}</CoarTab>
      </CoarTabGroup>

      <!-- Tab: Allgemein — identity + the App-link + IsRealmAdmin flag.
           The permission picker moves to its own tab so the role's
           grant surface gets full breathing room. -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <div class="modal-form">
          <!-- Section: Identität -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.roleDetails.section.identity', {}, 'Identity') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.roleDetails.name', {}, 'Name')" required>
                <CoarTextInput v-model="form.Name" clearable />
                <p class="field-hint">{{ t('admin.roleDetails.name.hint', {}, 'Display/identification name of the role.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.roleDetails.app', {}, 'Application')">
                <CoarSelect
                  v-model="form.AppId"
                  :options="appOptions"
                  @update:model-value="onAppIdChange"
                />
                <p class="field-hint">
                  {{ form.AppId
                    ? t('admin.roleDetails.app.linkedHint', {}, 'Role grants the selected permissions of this application.')
                    : t('admin.roleDetails.app.noneHint', {}, 'No application link — only the realm-admin flag below grants anything. Reserved for the System Admin role.') }}
                </p>
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('admin.roleDetails.description', {}, 'Description')">
                <CoarTextInput v-model="form.Description" clearable :rows="2" />
                <p class="field-hint">{{ t('admin.roleDetails.description.hint', {}, 'Optional note describing what this role is for.') }}</p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Berechtigung — Vorsicht (danger / global bypass, LAST). -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.roleDetails.section.danger', {}, 'Permissions — caution') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.roleDetails.isRealmAdmin.label', {}, 'Privileged role')">
                <CoarCheckbox
                  v-model="form.IsRealmAdmin"
                  :label="t('admin.roleDetails.isRealmAdmin.toggle', {}, 'System administrator (realm:admin)')"
                />
                <p class="field-hint">{{ t('admin.roleDetails.isRealmAdmin.hint', {}, 'Bypasses every permission check in every realm — only for the System Admin role.') }}</p>
                <CoarNote v-if="form.IsRealmAdmin" variant="warning">
                  {{ t('admin.roleDetails.isRealmAdmin.warning', {}, 'This flag grants realm:admin — the global bypass. Hand it out only to the System Admin role.') }}
                </CoarNote>
              </CoarFormField>
            </div>
          </section>
        </div>
      </div>

      <!-- Tab: Permissions — App-Catalog-Subset picker. Empty state
           covers both "no App linked" and "linked App's catalog empty"
           because they're admin-functionally the same: nothing to pick. -->
      <div v-show="activeTab === 'permissions'" class="tab-content">
        <p v-if="!form.AppId" class="text-sm text-gray-500">
          {{ t('admin.roleDetails.permissions.noApp', {}, 'This role isn\'t bound to any application — there\'s nothing to grant. Choose an app in the General tab, then its catalog will appear here.') }}
        </p>
        <template v-else>
          <p class="tab-hint">
            {{ t('admin.roleDetails.permissions.hint', {}, 'Subset of the app catalog. This role grants every checked permission to users assigned to it (via direct or group membership).') }}
          </p>
          <div v-if="linkedAppCatalog.length === 0" class="text-xs text-gray-400 italic">
            {{ t('admin.roleDetails.permissions.empty', {}, 'The selected Application has no entries in its catalog. Add entries via the App admin first.') }}
          </div>
          <div v-else class="permission-checklist">
            <div v-for="p in linkedAppCatalog" :key="p.Id" class="permission-row">
              <CoarCheckbox
                :model-value="form.PermissionIds.has(p.Id)"
                @update:model-value="() => togglePermissionId(p.Id)"
              />
              <span class="permission-label" @click="togglePermissionId(p.Id)">
                <code>{{ p.Resource }}:{{ p.Action }}</code>
                <span v-if="p.Description" class="permission-desc">— {{ p.Description }}</span>
              </span>
            </div>
          </div>
        </template>
      </div>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
.tab-bar {
  margin-bottom: 12px;
}
.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}
.tab-hint {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
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
.permission-checklist {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 280px;
  overflow-y: auto;
  padding: 8px;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
}
.permission-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 0.82rem;
  cursor: pointer;
}
.permission-row:hover {
  background: var(--coar-background-neutral-tertiary, #f3f4f6);
}
.permission-label {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
}
.permission-desc {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.78rem;
}
</style>
