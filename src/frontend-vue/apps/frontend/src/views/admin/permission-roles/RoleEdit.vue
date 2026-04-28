<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarSelect,
  CoarTag,
  CoarIcon,
  useToast,
  useDialog,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { usePermissionRoleStore } from '@/stores/permission-role.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = usePermissionRoleStore()
const toast = useToast()
const dialog = useDialog()
const { t } = useI18n()
const isCreate = computed(() => props.id === 'create')

const loading = ref(false)
const saving = ref(false)
const deleting = ref(false)
const saveError = ref<string | null>(null)

const form = ref({
  Name: '',
  Description: '',
  ResourceType: '',
  Permissions: [] as string[],
})

const permissionInput = ref('')

const KNOWN_RESOURCE_TYPES = [
  'tenant',
  'system',
  'user',
  'role',
  'oauth-client',
  'oauth-scope',
  'oauth-api',
  'login-provider',
  'permission-role',
  'authorization-group',
  'app',
]

const resourceTypeOptions = computed(() =>
  KNOWN_RESOURCE_TYPES.map(v => ({ value: v, label: v })),
)

const modalTitle = computed(() =>
  form.value.Name.trim()
    || (isCreate.value
      ? t('admin.permissionRoles.createTitle', {}, 'New Permission Role')
      : t('admin.permissionRoles.singular', {}, 'Permission Role')),
)

async function load() {
  if (isCreate.value) return
  loading.value = true
  try {
    const http = useHttpClient('/api/admin/permission-roles')
    const dto = await http.addPath(props.id).get<{
      Id: string
      Name: string
      Description?: string | null
      ResourceType: string
      Permissions: string[]
    }>()
    form.value = {
      Name: dto.Name,
      Description: dto.Description ?? '',
      ResourceType: dto.ResourceType,
      Permissions: [...dto.Permissions],
    }
  } catch (e) {
    saveError.value = extractErrorMessage(e) ?? t('admin.permissionRoles.loadFailed', {}, 'Failed to load permission role.')
  } finally {
    loading.value = false
  }
}

function addPermission() {
  const v = permissionInput.value.trim()
  if (!v) return
  if (!form.value.Permissions.includes(v)) {
    form.value.Permissions = [...form.value.Permissions, v]
  }
  permissionInput.value = ''
}

function removePermission(p: string) {
  form.value.Permissions = form.value.Permissions.filter(x => x !== p)
}

function onPermissionKey(e: KeyboardEvent) {
  if (e.key === 'Enter' || e.key === ',') {
    e.preventDefault()
    addPermission()
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
  if (!form.value.Name.trim() || !form.value.ResourceType.trim()) return
  saving.value = true
  saveError.value = null
  const http = useHttpClient('/api/admin/permission-roles')
  const dto = {
    Name: form.value.Name.trim(),
    Description: form.value.Description.trim() || undefined,
    ResourceType: form.value.ResourceType.trim(),
    Permissions: form.value.Permissions,
  }
  try {
    if (isCreate.value) {
      await http.post(dto)
      toast.success(t('admin.permissionRoles.created', {}, 'Permission role created.'))
    } else {
      await http.addPath(props.id).put(dto)
      toast.success(t('admin.permissionRoles.saved', {}, 'Permission role saved.'))
    }
    await store.loadAll()
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
    title: t('admin.permissionRoles.deleteTitle', {}, 'Delete Permission Role'),
    message: t('admin.permissionRoles.deleteMessage', {}, 'Delete this permission role? This cannot be undone.'),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await ref.result
  if (!ok) return

  deleting.value = true
  try {
    const http = useHttpClient('/api/admin/permission-roles')
    await http.addPath(props.id).delete()
    toast.success(t('admin.permissionRoles.deleted', {}, 'Permission role deleted.'))
    await store.loadAll()
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
  disabled: !form.value.Name.trim() || !form.value.ResourceType.trim() || saving.value,
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

onMounted(load)
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    icon="shield"
    :footer-button="footerButton"
    :footer-delete-button="footerDeleteButton"
    width="36rem"
  >
    <div v-if="loading" class="center">{{ t('common.loading', {}, 'Loading...') }}</div>
    <div v-else class="form">
      <div v-if="saveError" class="error-banner">
        <div class="error-title">{{ t('common.operationFailed', {}, 'Operation failed') }}</div>
        <pre class="error-message">{{ saveError }}</pre>
        <button type="button" class="error-dismiss" @click="saveError = null">&times;</button>
      </div>

      <section>
        <div class="section-heading">{{ t('common.general', {}, 'General') }}</div>
        <CoarFormField :label="t('common.name', {}, 'Name')" required>
          <CoarTextInput v-model="form.Name" clearable :placeholder="t('admin.permissionRoles.namePlaceholder', {}, 'role-name')" />
        </CoarFormField>
        <CoarFormField :label="t('common.description', {}, 'Description')">
          <CoarTextInput v-model="form.Description" clearable :placeholder="t('admin.permissionRoles.descriptionPlaceholder', {}, 'What this role grants...')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.permissionRoles.resourceType', {}, 'Resource Type')" required>
          <CoarSelect
            v-model="form.ResourceType"
            :options="resourceTypeOptions"
            :placeholder="t('admin.permissionRoles.resourceTypePlaceholder', {}, 'Pick a resource type')"
            :disabled="!isCreate"
          />
        </CoarFormField>
      </section>

      <section>
        <div class="section-heading">{{ t('admin.permissionRoles.permissions', {}, 'Permissions') }}</div>
        <div v-if="form.Permissions.length > 0" class="perm-chips">
          <CoarTag
            v-for="p in form.Permissions"
            :key="p"
            size="s"
            variant="neutral"
            removable
            @remove="removePermission(p)"
          >
            <template #icon>
              <CoarIcon name="key-round" size="s" />
            </template>
            {{ p }}
          </CoarTag>
        </div>
        <div class="perm-add">
          <CoarTextInput
            v-model="permissionInput"
            :placeholder="t('admin.permissionRoles.permissionPlaceholder', {}, 'Permission (e.g. read, write, admin)')"
            size="s"
            @keydown="onPermissionKey"
          />
          <button type="button" class="add-btn" :disabled="!permissionInput.trim()" @click="addPermission">{{ t('common.add', {}, 'Add') }}</button>
        </div>
        <p class="hint">{{ t('admin.permissionRoles.permissionHint', {}, 'Enter a permission and press Enter or comma to add it.') }}</p>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.center {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
  color: var(--coar-text-neutral-secondary, #64748b);
}

.section-heading {
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #64748b);
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  padding-bottom: 4px;
  margin-bottom: 10px;
}

section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.perm-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.perm-add {
  display: flex;
  gap: 6px;
  align-items: center;
}

.add-btn {
  padding: 4px 12px;
  height: 28px;
  border: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  border-radius: var(--coar-radius-s, 3px);
  background: var(--coar-background-neutral-primary, white);
  font-size: 0.8125rem;
  cursor: pointer;
}

.add-btn:hover:not([disabled]) {
  background: var(--coar-background-neutral-tertiary, #f1f5f9);
}
.add-btn[disabled] { opacity: 0.5; cursor: not-allowed; }

.hint {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #64748b);
  margin: 0;
}

kbd {
  display: inline-block;
  padding: 1px 5px;
  font-size: 0.7rem;
  border: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  border-radius: 3px;
  background: var(--coar-background-neutral-tertiary, #f1f5f9);
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
