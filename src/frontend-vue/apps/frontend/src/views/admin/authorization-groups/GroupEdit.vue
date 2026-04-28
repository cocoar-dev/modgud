<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarSelect,
  CoarRadioGroup,
  CoarRadioButton,
  CoarTabGroup,
  CoarTab,
  CoarNote,
  useToast,
  useDialog,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import PrincipalPicker from '@/components/PrincipalPicker.vue'
import PermissionRolePicker from '@/components/PermissionRolePicker.vue'
import ScriptEditor from '@/components/ScriptEditor.vue'
import {
  useAuthorizationGroupStore,
  type MembershipMode,
  type EmailMode,
  type ResourceAccessScriptDto,
  type AuthorizationGroupDto,
} from '@/stores/authorization-group.store'
import { usePermissionRoleStore } from '@/stores/permission-role.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const groupStore = useAuthorizationGroupStore()
const roleStore = usePermissionRoleStore()
const toast = useToast()
const dialog = useDialog()
const { t } = useI18n()

const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const saving = ref(false)
const deleting = ref(false)
const saveError = ref<string | null>(null)

type TabId = 'general' | 'members' | 'roles' | 'access' | 'script'
const activeTab = ref<TabId>('general')

const form = ref({
  Name: '',
  Description: '',
  MemberIds: [] as string[],
  RoleIds: [] as string[],
  AccessScripts: [] as ResourceAccessScriptDto[],
  MembershipMode: 'Manual' as MembershipMode,
  MembershipScript: '',
  MembershipLastError: null as string | null,
  Email: '',
  EmailMode: 'Shared' as EmailMode,
})

const modalTitle = computed(() =>
  form.value.Name.trim()
    || (isCreate.value
      ? t('admin.groups.createTitle', {}, 'New Authorization Group')
      : t('admin.groups.singular', {}, 'Authorization Group')),
)

const isAutoMode = computed(() => form.value.MembershipMode === 'Auto')

watch(isAutoMode, (auto) => {
  if (!auto && activeTab.value === 'script') activeTab.value = 'members'
})

// Compute resource types that need access scripts based on selected roles
// (excludes 'app' which is a global admin role).
const requiredResourceTypes = computed(() => {
  const types = new Set<string>()
  for (const roleId of form.value.RoleIds) {
    const role = roleStore.entities.find(r => r.Id === roleId)
    if (role && role.ResourceType && role.ResourceType !== 'app') {
      types.add(role.ResourceType)
    }
  }
  return [...types].sort()
})

const hasAccessConfig = computed(() => requiredResourceTypes.value.length > 0)

watch(hasAccessConfig, (has) => {
  if (!has && activeTab.value === 'access') activeTab.value = 'general'
})

function syncAccessScripts() {
  const existing = new Map(form.value.AccessScripts.map(s => [s.ResourceType, s]))
  form.value.AccessScripts = requiredResourceTypes.value.map(rt =>
    existing.get(rt) ?? { ResourceType: rt, Script: '' },
  )
}

function getScript(rt: string): string {
  return form.value.AccessScripts.find(s => s.ResourceType === rt)?.Script ?? ''
}

function setScript(rt: string, value: string) {
  const existing = form.value.AccessScripts.find(s => s.ResourceType === rt)
  if (existing) {
    existing.Script = value
  } else {
    form.value.AccessScripts.push({ ResourceType: rt, Script: value })
  }
}

async function load() {
  loading.value = true
  try {
    if (roleStore.entities.length === 0) {
      roleStore.initialize()
      await roleStore.loadAll()
    }

    if (!isCreate.value) {
      const http = useHttpClient('/api/admin/authorization-groups')
      const dto = await http.addPath(props.id).get<AuthorizationGroupDto>()
      form.value = {
        Name: dto.Name,
        Description: dto.Description ?? '',
        MemberIds: [...(dto.MemberIds ?? [])],
        RoleIds: [...(dto.RoleIds ?? [])],
        AccessScripts: (dto.AccessScripts ?? []).map(s => ({
          ResourceType: s.ResourceType,
          Script: s.Script ?? '',
        })),
        MembershipMode: (dto.MembershipMode as MembershipMode) ?? 'Manual',
        MembershipScript: dto.MembershipScript ?? '',
        MembershipLastError: dto.MembershipLastError ?? null,
        Email: dto.Email ?? '',
        EmailMode: (dto.EmailMode as EmailMode) ?? 'Shared',
      }
    }
  } catch (e) {
    saveError.value = extractErrorMessage(e) ?? t('admin.groups.loadFailed', {}, 'Failed to load group.')
  } finally {
    loading.value = false
  }
}

function extractErrorMessage(e: unknown): string | null {
  if (e instanceof HttpClientError) {
    const body: any = e.body
    if (body && typeof body === 'object') {
      if (Array.isArray(body.Errors) && body.Errors.length > 0) {
        return body.Errors.map((err: any) => err?.Description ?? err?.description ?? '').filter(Boolean).join('\n')
      }
      return body.detail ?? body.title ?? body.error ?? null
    }
    if (typeof body === 'string') return body
    return e.message
  }
  return e instanceof Error ? e.message : null
}

async function save() {
  if (!form.value.Name.trim()) return
  if (isAutoMode.value && !form.value.MembershipScript.trim()) return
  saving.value = true
  saveError.value = null
  try {
    syncAccessScripts()
    const dto = {
      Name: form.value.Name.trim(),
      Description: form.value.Description.trim() || undefined,
      MemberIds: isAutoMode.value ? [] : form.value.MemberIds,
      RoleIds: form.value.RoleIds,
      AccessScripts: form.value.AccessScripts,
      MembershipMode: form.value.MembershipMode,
      MembershipScript: isAutoMode.value ? form.value.MembershipScript : undefined,
      Email: form.value.Email.trim() || undefined,
      EmailMode: form.value.EmailMode,
    }
    const http = useHttpClient('/api/admin/authorization-groups')
    let saved: AuthorizationGroupDto
    if (isCreate.value) {
      saved = await http.post<AuthorizationGroupDto>(dto)
      toast.success(t('admin.groups.created', {}, 'Group created.'))
    } else {
      saved = await http.addPath(props.id).put<AuthorizationGroupDto>(dto)
      toast.success(t('admin.groups.saved', {}, 'Group saved.'))
    }
    await groupStore.loadAll()

    // If the membership script failed, keep the modal open and surface the error.
    if (saved?.MembershipLastError) {
      form.value.MembershipLastError = saved.MembershipLastError
      return
    }
    props.close({ saved: true })
  } catch (e) {
    saveError.value = extractErrorMessage(e) ?? t('common.saveFailed', {}, 'Save failed.')
  } finally {
    saving.value = false
  }
}

async function confirmDelete() {
  if (isCreate.value) return
  const ref = dialog.confirm({
    title: t('admin.groups.deleteTitle', {}, 'Delete Authorization Group'),
    message: t('admin.groups.deleteMessageSingle', {}, 'Delete this authorization group? This cannot be undone.'),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await ref.result
  if (!ok) return

  deleting.value = true
  try {
    const http = useHttpClient('/api/admin/authorization-groups')
    await http.addPath(props.id).delete()
    toast.success(t('admin.groups.deleted', {}, 'Group deleted.'))
    await groupStore.loadAll()
    props.close({ deleted: true })
  } catch (e) {
    saveError.value = extractErrorMessage(e) ?? t('common.deleteFailed', {}, 'Delete failed.')
  } finally {
    deleting.value = false
  }
}

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim()
    || saving.value
    || (isAutoMode.value && !form.value.MembershipScript.trim()),
  loading: saving.value,
  onClick: save,
}))

const footerDeleteButton = computed(() =>
  isCreate.value
    ? undefined
    : {
        visible: true,
        text: t('common.delete', {}, 'Delete'),
        disabled: deleting.value || saving.value,
        loading: deleting.value,
        onClick: confirmDelete,
      },
)

const membershipModeOptions = computed(() => [
  { value: 'Manual', label: t('admin.groups.modeManual', {}, 'Manual') },
  { value: 'Auto', label: t('admin.groups.modeAuto', {}, 'Automatic (script)') },
])

onMounted(load)
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    icon="users-round"
    :footer-button="footerButton"
    :footer-delete-button="footerDeleteButton"
    width="44rem"
  >
    <div v-if="loading" class="center">{{ t('common.loading', {}, 'Loading...') }}</div>
    <div v-else class="form">
      <div v-if="form.MembershipLastError" class="error-banner">
        <div class="error-title">{{ t('admin.groups.scriptErrorTitle', {}, 'Membership script error (last evaluation)') }}</div>
        <pre class="error-message">{{ form.MembershipLastError }}</pre>
      </div>

      <div v-if="saveError" class="error-banner">
        <div class="error-title">{{ t('common.operationFailed', {}, 'Operation failed') }}</div>
        <pre class="error-message">{{ saveError }}</pre>
        <button type="button" class="error-dismiss" @click="saveError = null">&times;</button>
      </div>

      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('common.general', {}, 'General') }}</CoarTab>
        <CoarTab id="members">{{ t('admin.groups.membersTab', {}, 'Members') }}</CoarTab>
        <CoarTab v-if="isAutoMode" id="script">{{ t('admin.groups.scriptTab', {}, 'Script') }}</CoarTab>
        <CoarTab id="roles">{{ t('admin.groups.rolesTab', {}, 'Roles') }}</CoarTab>
        <CoarTab v-if="hasAccessConfig" id="access">{{ t('admin.groups.accessTab', {}, 'Access') }}</CoarTab>
      </CoarTabGroup>

      <!-- General -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <section>
          <CoarFormField :label="t('common.name', {}, 'Name')" required>
            <CoarTextInput v-model="form.Name" clearable :placeholder="t('admin.groups.namePlaceholder', {}, 'Group name')" />
          </CoarFormField>
          <CoarFormField :label="t('common.description', {}, 'Description')">
            <CoarTextInput v-model="form.Description" clearable :placeholder="t('admin.groups.descriptionPlaceholder', {}, 'What this group is for...')" />
          </CoarFormField>
          <CoarFormField :label="t('common.email', {}, 'Email')">
            <CoarTextInput v-model="form.Email" clearable placeholder="team@example.com" />
          </CoarFormField>
          <CoarFormField :label="t('admin.groups.emailMode', {}, 'Email Mode')">
            <CoarRadioGroup v-model="form.EmailMode" orientation="horizontal">
              <CoarRadioButton value="Shared">{{ t('admin.groups.emailModeShared', {}, 'Shared (group mailbox)') }}</CoarRadioButton>
              <CoarRadioButton value="ExpandToMembers">{{ t('admin.groups.emailModeExpand', {}, 'Expand to members') }}</CoarRadioButton>
            </CoarRadioGroup>
            <p class="hint">
              <span v-if="form.EmailMode === 'Shared'">{{ t('admin.groups.emailModeSharedHint', {}, 'Notifications go to this address.') }}</span>
              <span v-else>{{ t('admin.groups.emailModeExpandHint', {}, 'Notifications are sent to each member individually.') }}</span>
            </p>
          </CoarFormField>
          <CoarFormField :label="t('admin.groups.membershipMode', {}, 'Membership Mode')">
            <CoarSelect v-model="form.MembershipMode" :options="membershipModeOptions" />
            <p class="hint">
              <span v-if="isAutoMode">{{ t('admin.groups.membershipAutoHint', {}, 'Members are computed from the script in the Script tab.') }}</span>
              <span v-else>{{ t('admin.groups.membershipManualHint', {}, 'Pick members directly in the Members tab.') }}</span>
            </p>
          </CoarFormField>
        </section>
      </div>

      <!-- Members -->
      <div v-show="activeTab === 'members'" class="tab-content">
        <section>
          <div class="section-heading">{{ t('admin.groups.membersTab', {}, 'Members') }}</div>
          <template v-if="!isAutoMode">
            <PrincipalPicker
              v-model="form.MemberIds"
              :placeholder="t('admin.groups.searchPrincipals', {}, 'Search people and groups...')"
              :exclude-ids="[props.id].filter(x => x !== 'create')"
            />
            <p class="hint">{{ t('admin.groups.membersHint', {}, 'Add people or nested groups. Nested groups resolve recursively at evaluation time.') }}</p>
          </template>
          <template v-else>
            <CoarNote variant="info" padding="sm">
              {{ t('admin.groups.membersAutoNote', {}, 'Members are computed from the script in the Script tab.') }}
            </CoarNote>
          </template>
        </section>
      </div>

      <!-- Script -->
      <div v-show="activeTab === 'script'" class="tab-content">
        <section>
          <div class="section-heading">{{ t('admin.groups.membershipScript', {}, 'Membership Script') }}</div>
          <p class="hint">
            {{ t('admin.groups.membershipScriptHint', {}, 'TypeScript arrow function returning true for principals that should be members.') }}
          </p>
          <ScriptEditor
            v-model="form.MembershipScript"
            height="240px"
            placeholder="(p) => p.Type === 'Person' && p.IsActive"
          />
        </section>
      </div>

      <!-- Roles -->
      <div v-show="activeTab === 'roles'" class="tab-content">
        <section>
          <div class="section-heading">{{ t('admin.groups.permissionRoles', {}, 'Permission Roles') }}</div>
          <p class="hint">{{ t('admin.groups.permissionRolesHint', {}, 'Assign permission roles. Each resource type gets its own access script below.') }}</p>
          <PermissionRolePicker
            :model-value="form.RoleIds"
            @update:model-value="(v: string[]) => { form.RoleIds = v; syncAccessScripts() }"
          />
        </section>
      </div>

      <!-- Access -->
      <div v-show="activeTab === 'access'" class="tab-content">
        <section>
          <div class="section-heading">{{ t('admin.groups.accessScripts', {}, 'Access Scripts') }}</div>
          <p class="hint">{{ t('admin.groups.accessScriptsHint', {}, 'Restrict which resources members can see per resource type. Empty = no restriction.') }}</p>
          <div v-if="requiredResourceTypes.length === 0" class="empty-hint">
            {{ t('admin.groups.accessEmpty', {}, 'Assign roles to configure per-resource access.') }}
          </div>
          <div v-else class="script-list">
            <div v-for="rt in requiredResourceTypes" :key="rt" class="script-block">
              <div class="script-label">{{ rt }}</div>
              <ScriptEditor
                :model-value="getScript(rt)"
                @update:model-value="setScript(rt, $event)"
                height="160px"
                :placeholder="`(x) => /* condition for ${rt} */`"
              />
            </div>
          </div>
        </section>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-height: 420px;
}

.center {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
  color: var(--coar-text-neutral-secondary, #64748b);
}

.tab-bar {
  margin-bottom: 6px;
}

.tab-content {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 2px 2px 16px;
  min-height: 0;
}

section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.section-heading {
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #64748b);
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  padding-bottom: 4px;
  margin-bottom: 4px;
}

.hint {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #64748b);
  margin: 0;
}

.empty-hint {
  padding: 16px;
  text-align: center;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary, #64748b);
  font-style: italic;
}

.script-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.script-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.script-label {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--coar-text-neutral-primary, #0f172a);
  text-transform: capitalize;
}

.error-banner {
  position: relative;
  padding: 10px 32px 10px 14px;
  background: var(--coar-background-semantic-error-subtle, #fef2f2);
  border: 1px solid var(--coar-border-semantic-error, #fca5a5);
  border-radius: var(--coar-radius-m, 4px);
}

.error-title {
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--coar-text-semantic-error-bold, #b91c1c);
  margin-bottom: 4px;
}

.error-message {
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
  color: var(--coar-text-semantic-error-bold, #991b1b);
  white-space: pre-wrap;
  word-break: break-word;
  margin: 0;
}

.error-dismiss {
  position: absolute;
  top: 6px;
  right: 8px;
  width: 22px;
  height: 22px;
  border: none;
  background: transparent;
  color: var(--coar-text-semantic-error-bold, #991b1b);
  font-size: 1.1rem;
  cursor: pointer;
  border-radius: 3px;
}
.error-dismiss:hover {
  background: rgba(185, 28, 28, 0.1);
}
</style>
