<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarBadge,
  useToast,
  useDialog,
} from '@cocoar/vue-ui'
import { useI18n, useL10n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const toast = useToast()
const dialog = useDialog()
const { t } = useI18n()
const { fmtDate } = useL10n()
const isCreate = computed(() => props.id === 'create')

interface RoleDto {
  Id: string
  Name: string
  Description?: string | null
  DisplayName?: string | null
  Email?: string | null
  ClientId?: string | null
  Scopes: string[]
  CreatedAt?: string | null
  ModifiedAt?: string | null
}

const loading = ref(!isCreate.value)
const saving = ref(false)
const deleting = ref(false)
const nameError = ref('')

const form = ref({
  Name: '',
  DisplayName: '',
  Description: '',
  Email: '',
  Scopes: '',
})
const createdAt = ref<string | null>(null)
const modifiedAt = ref<string | null>(null)
const clientId = ref<string | null>(null)

const title = computed(() => {
  if (isCreate.value) return t('admin.roles.createTitle', {}, 'Create Role')
  return form.value.DisplayName || form.value.Name || t('admin.roles.singular', {}, 'Role')
})
const subTitle = computed(() => isCreate.value ? undefined : form.value.Name)

const canSave = computed(() => form.value.Name.trim().length > 0)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  loading: saving.value,
  disabled: !canSave.value || loading.value,
  onClick: save,
}))

const footerDeleteButton = computed(() =>
  isCreate.value ? undefined : {
    visible: true,
    text: t('common.delete', {}, 'Delete'),
    loading: deleting.value,
    disabled: saving.value,
    onClick: confirmDelete,
  },
)

watch(() => form.value.Name, () => { nameError.value = '' })

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const http = useHttpClient('/api/admin/roles')
    const r = await http.addPath(props.id).get<RoleDto>()
    form.value = {
      Name: r.Name,
      DisplayName: r.DisplayName ?? '',
      Description: r.Description ?? '',
      Email: r.Email ?? '',
      Scopes: (r.Scopes ?? []).join(', '),
    }
    createdAt.value = r.CreatedAt ?? null
    modifiedAt.value = r.ModifiedAt ?? null
    clientId.value = r.ClientId ?? null
  } catch (e) {
    toast.error(e instanceof HttpClientError ? `Failed to load role (HTTP ${e.status}).` : 'Failed to load role.')
    props.close()
  } finally {
    loading.value = false
  }
})

function parseScopes(): string[] {
  return form.value.Scopes
    .split(/[,\n]/)
    .map(s => s.trim())
    .filter(Boolean)
}

async function save() {
  if (!canSave.value) return
  saving.value = true
  try {
    const http = useHttpClient('/api/admin/roles')
    if (isCreate.value) {
      const created = await http.post<RoleDto>({
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName || null,
        Description: form.value.Description || null,
        Email: form.value.Email || null,
        Scopes: parseScopes(),
      })
      toast.success(t('admin.roles.created', {}, 'Role created.'))
      props.close(created)
    } else {
      const updated = await http.addPath(props.id).patch<RoleDto>({
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName || null,
        Description: form.value.Description || null,
        Email: form.value.Email || null,
        Scopes: parseScopes(),
      })
      toast.success(t('admin.roles.saved', {}, 'Role saved.'))
      props.close(updated)
    }
  } catch (e) {
    if (e instanceof HttpClientError && e.status === 409) {
      nameError.value = t('admin.roles.nameInUse', {}, 'A role with this name already exists.')
    } else {
      const msg = e instanceof HttpClientError
        ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? `Save failed (HTTP ${e.status}).`)
        : t('common.saveFailed', {}, 'Save failed.')
      toast.error(String(msg))
    }
  } finally {
    saving.value = false
  }
}

async function confirmDelete() {
  const result = dialog.confirm({
    title: t('admin.roles.deleteTitle', {}, 'Delete Role'),
    message: t('admin.roles.deleteMessage', { name: form.value.Name }, `Delete role "${form.value.Name}"? This cannot be undone.`),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return
  deleting.value = true
  try {
    const http = useHttpClient('/api/admin/roles')
    await http.addPath(props.id).delete()
    toast.success(t('admin.roles.deleted', {}, 'Role deleted.'))
    props.close({ deleted: true, id: props.id })
  } catch (e) {
    const msg = e instanceof HttpClientError
      ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? `Delete failed (HTTP ${e.status}).`)
      : t('common.deleteFailed', {}, 'Delete failed.')
    toast.error(String(msg))
  } finally {
    deleting.value = false
  }
}

function formatOrDash(v: string | null): string {
  return v ? fmtDate(v, true) : '—'
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="title"
    :sub-title="subTitle"
    icon="shield-check"
    width="38rem"
    :footer-button="footerButton"
    :footer-delete-button="footerDeleteButton"
  >
    <div v-if="loading" class="center">{{ t('common.loading', {}, 'Loading…') }}</div>
    <div v-else class="form">
      <CoarFormField :label="t('common.name', {}, 'Name')" required :error="nameError || undefined">
        <CoarTextInput v-model="form.Name" clearable />
      </CoarFormField>

      <CoarFormField :label="t('common.displayName', {}, 'Display Name')">
        <CoarTextInput v-model="form.DisplayName" clearable />
      </CoarFormField>

      <CoarFormField :label="t('common.description', {}, 'Description')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>

      <CoarFormField :label="t('common.email', {}, 'Email')">
        <CoarTextInput v-model="form.Email" type="email" clearable />
      </CoarFormField>

      <CoarFormField :label="t('admin.roles.scopes', {}, 'Scopes')" :hint="t('admin.roles.scopesHint', {}, 'Comma- or line-separated OAuth scope names.')">
        <CoarTextInput v-model="form.Scopes" clearable />
      </CoarFormField>

      <section v-if="!isCreate">
        <div class="section-heading">{{ t('common.metadata', {}, 'Metadata') }}</div>
        <div class="field-grid">
          <div class="field">
            <div class="field-label">{{ t('admin.roles.client', {}, 'Client') }}</div>
            <div class="field-value">
              <code v-if="clientId">{{ clientId }}</code>
              <CoarBadge v-else variant="info" size="s">{{ t('admin.roles.realm', {}, 'realm') }}</CoarBadge>
            </div>
          </div>
          <div class="field"><div class="field-label">{{ t('common.created', {}, 'Created') }}</div><div class="field-value">{{ formatOrDash(createdAt) }}</div></div>
          <div class="field"><div class="field-label">{{ t('common.modified', {}, 'Modified') }}</div><div class="field-value">{{ formatOrDash(modifiedAt) }}</div></div>
        </div>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-height: 280px;
}
.center {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
  color: var(--coar-text-neutral-secondary, #64748b);
}

section { display: flex; flex-direction: column; gap: 8px; margin-top: 4px; }
.section-heading {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  padding-bottom: 4px;
  margin-bottom: 4px;
}
.field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px 18px; }
.field { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.field-label {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b);
  font-weight: 500;
}
.field-value {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-primary, #0f172a);
  display: flex;
  align-items: center;
  gap: 6px;
}
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
