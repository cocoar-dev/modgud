<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import {
  CoarTextInput,
  CoarPasswordInput,
  CoarFormField,
  CoarTabGroup,
  CoarTab,
  CoarSwitch,
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

interface UserDetailsDto {
  Id: string
  UserName: string
  Email?: string | null
  EmailConfirmed?: boolean
  PhoneNumber?: string | null
  PhoneNumberConfirmed?: boolean
  TwoFactorEnabled?: boolean
  LockoutEnd?: string | null
  LockoutEnabled?: boolean
  AccessFailedCount?: number
  FirstName?: string | null
  LastName?: string | null
  ExpiresAt?: string | null
  IsActive: boolean
  CreatedAt?: string | null
  ModifiedAt?: string | null
  Roles: string[]
}

type TabId = 'general' | 'security'
const activeTab = ref<TabId>('general')
const loading = ref(!isCreate.value)
const saving = ref(false)
const deleting = ref(false)
const userNameError = ref('')

const form = ref({
  UserName: '',
  Email: '',
  PhoneNumber: '',
  FirstName: '',
  LastName: '',
  Password: '',
  IsActive: true,
  LockoutEnabled: true,
  EmailConfirmed: false,
  PhoneNumberConfirmed: false,
})

const original = ref<UserDetailsDto | null>(null)
const lockoutEnd = ref<string | null>(null)
const accessFailedCount = ref(0)
const twoFactorEnabled = ref(false)
const createdAt = ref<string | null>(null)
const modifiedAt = ref<string | null>(null)

const title = computed(() => {
  if (isCreate.value) return t('admin.users.createTitle', {}, 'Create User')
  const f = form.value
  const name = [f.FirstName, f.LastName].filter(Boolean).join(' ')
  return name || f.UserName || t('admin.users.singular', {}, 'User')
})
const subTitle = computed(() => isCreate.value ? undefined : form.value.UserName)

const canSave = computed(() => {
  if (!form.value.UserName.trim()) return false
  if (isCreate.value && !form.value.Password) return false
  return true
})

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

watch(() => form.value.UserName, () => { userNameError.value = '' })

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const http = useHttpClient('/api/admin/users')
    const u = await http.addPath(props.id).get<UserDetailsDto>()
    original.value = u
    form.value = {
      UserName: u.UserName,
      Email: u.Email ?? '',
      PhoneNumber: u.PhoneNumber ?? '',
      FirstName: u.FirstName ?? '',
      LastName: u.LastName ?? '',
      Password: '',
      IsActive: u.IsActive,
      LockoutEnabled: u.LockoutEnabled ?? true,
      EmailConfirmed: u.EmailConfirmed ?? false,
      PhoneNumberConfirmed: u.PhoneNumberConfirmed ?? false,
    }
    lockoutEnd.value = u.LockoutEnd ?? null
    accessFailedCount.value = u.AccessFailedCount ?? 0
    twoFactorEnabled.value = u.TwoFactorEnabled ?? false
    createdAt.value = u.CreatedAt ?? null
    modifiedAt.value = u.ModifiedAt ?? null
  } catch (e) {
    toast.error(e instanceof HttpClientError ? `Failed to load user (HTTP ${e.status}).` : 'Failed to load user.')
    props.close()
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!canSave.value) return
  saving.value = true
  try {
    const http = useHttpClient('/api/admin/users')
    if (isCreate.value) {
      const created = await http.post<UserDetailsDto>({
        UserName: form.value.UserName.trim(),
        Password: form.value.Password,
        Email: form.value.Email || null,
        PhoneNumber: form.value.PhoneNumber || null,
        FirstName: form.value.FirstName || null,
        LastName: form.value.LastName || null,
        IsActive: form.value.IsActive,
        LockoutEnabled: form.value.LockoutEnabled,
      })
      toast.success(t('admin.users.created', {}, 'User created.'))
      props.close(created)
    } else {
      const updated = await http.addPath(props.id).patch<UserDetailsDto>({
        UserName: form.value.UserName.trim(),
        Email: form.value.Email || null,
        PhoneNumber: form.value.PhoneNumber || null,
        FirstName: form.value.FirstName || null,
        LastName: form.value.LastName || null,
        IsActive: form.value.IsActive,
        LockoutEnabled: form.value.LockoutEnabled,
        EmailConfirmed: form.value.EmailConfirmed,
        PhoneNumberConfirmed: form.value.PhoneNumberConfirmed,
      })
      toast.success(t('admin.users.saved', {}, 'User saved.'))
      props.close(updated)
    }
  } catch (e) {
    if (e instanceof HttpClientError && e.status === 409) {
      userNameError.value = t('admin.users.nameInUse', {}, 'Username or email already in use.')
      activeTab.value = 'general'
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
    title: t('admin.users.deleteTitle', {}, 'Delete User'),
    message: t('admin.users.deleteMessage', { name: form.value.UserName }, `Delete user "${form.value.UserName}"? This soft-deletes the account.`),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return
  deleting.value = true
  try {
    const http = useHttpClient('/api/admin/users')
    await http.addPath(props.id).delete()
    toast.success(t('admin.users.deleted', {}, 'User deleted.'))
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

async function unlock() {
  try {
    const http = useHttpClient('/api/admin/users')
    await http.addPath(props.id).addPath('unlock').post({})
    lockoutEnd.value = null
    accessFailedCount.value = 0
    toast.success(t('admin.users.unlocked', {}, 'User unlocked.'))
  } catch (e) {
    toast.error(e instanceof HttpClientError ? `Unlock failed (HTTP ${e.status}).` : t('admin.users.unlockFailed', {}, 'Unlock failed.'))
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
    icon="user"
    width="42rem"
    :footer-button="footerButton"
    :footer-delete-button="footerDeleteButton"
  >
    <div v-if="loading" class="center">{{ t('common.loading', {}, 'Loading…') }}</div>
    <div v-else class="form">
      <CoarTabGroup v-if="!isCreate" v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('common.general', {}, 'General') }}</CoarTab>
        <CoarTab id="security">{{ t('common.security', {}, 'Security') }}</CoarTab>
      </CoarTabGroup>

      <div v-show="isCreate || activeTab === 'general'" class="tab-content">
        <div class="form-row">
          <CoarFormField :label="t('admin.users.firstName', {}, 'First Name')" class="flex-1">
            <CoarTextInput v-model="form.FirstName" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.users.lastName', {}, 'Last Name')" class="flex-1">
            <CoarTextInput v-model="form.LastName" clearable />
          </CoarFormField>
        </div>

        <CoarFormField :label="t('common.username', {}, 'Username')" required :error="userNameError || undefined">
          <CoarTextInput v-model="form.UserName" clearable />
        </CoarFormField>

        <CoarFormField :label="t('common.email', {}, 'Email')">
          <CoarTextInput v-model="form.Email" type="email" clearable />
        </CoarFormField>

        <CoarFormField :label="t('admin.users.phoneNumber', {}, 'Phone Number')">
          <CoarTextInput v-model="form.PhoneNumber" clearable />
        </CoarFormField>

        <CoarFormField v-if="isCreate" :label="t('admin.users.initialPassword', {}, 'Initial Password')" required>
          <CoarPasswordInput v-model="form.Password" />
        </CoarFormField>

        <div class="toggle-row">
          <CoarSwitch v-model="form.IsActive" :label="t('common.active', {}, 'Active')" />
          <CoarSwitch v-if="!isCreate" v-model="form.EmailConfirmed" :label="t('admin.users.emailConfirmed', {}, 'Email confirmed')" />
          <CoarSwitch v-if="!isCreate" v-model="form.PhoneNumberConfirmed" :label="t('admin.users.phoneConfirmed', {}, 'Phone confirmed')" />
        </div>
      </div>

      <div v-show="!isCreate && activeTab === 'security'" class="tab-content">
        <section>
          <div class="section-heading">{{ t('admin.users.lockout', {}, 'Lockout') }}</div>
          <div class="field-grid">
            <div class="field">
              <div class="field-label">{{ t('common.status', {}, 'Status') }}</div>
              <div class="field-value">
                <CoarBadge v-if="lockoutEnd" variant="warning" size="s">{{ t('admin.users.locked', {}, 'locked') }}</CoarBadge>
                <CoarBadge v-else variant="success" size="s">{{ t('admin.users.unlocked', {}, 'unlocked') }}</CoarBadge>
              </div>
            </div>
            <div class="field">
              <div class="field-label">{{ t('admin.users.failedAttempts', {}, 'Failed Attempts') }}</div>
              <div class="field-value">{{ accessFailedCount }}</div>
            </div>
            <div class="field">
              <div class="field-label">{{ t('admin.users.lockoutEnd', {}, 'Lockout End') }}</div>
              <div class="field-value">{{ formatOrDash(lockoutEnd) }}</div>
            </div>
            <div class="field">
              <div class="field-label">{{ t('admin.users.twoFactorEnabled', {}, '2FA Enabled') }}</div>
              <div class="field-value">
                <CoarBadge :variant="twoFactorEnabled ? 'info' : 'neutral'" size="s">
                  {{ twoFactorEnabled ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}
                </CoarBadge>
              </div>
            </div>
          </div>
          <div class="actions-row">
            <CoarSwitch v-model="form.LockoutEnabled" :label="t('admin.users.lockoutEnabledHint', {}, 'Lockout enabled (account auto-locks on failed attempts)')" />
            <button v-if="lockoutEnd" type="button" class="link-btn" @click="unlock">{{ t('admin.users.unlockNow', {}, 'Unlock now') }}</button>
          </div>
        </section>

        <section>
          <div class="section-heading">{{ t('common.timestamps', {}, 'Timestamps') }}</div>
          <div class="field-grid">
            <div class="field"><div class="field-label">{{ t('common.created', {}, 'Created') }}</div><div class="field-value">{{ formatOrDash(createdAt) }}</div></div>
            <div class="field"><div class="field-label">{{ t('common.modified', {}, 'Modified') }}</div><div class="field-value">{{ formatOrDash(modifiedAt) }}</div></div>
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
  min-height: 320px;
}

.center {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
  color: var(--coar-text-neutral-secondary, #64748b);
}

.tab-bar { margin-bottom: 6px; }

.tab-content {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 2px 2px 16px;
  min-height: 0;
}

.form-row {
  display: flex;
  gap: 12px;
}

.toggle-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-top: 4px;
}

section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

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

.field-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 18px;
}

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

.actions-row {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-top: 6px;
}

.link-btn {
  background: none;
  border: none;
  color: var(--coar-text-link, #2563eb);
  cursor: pointer;
  font-size: 0.875rem;
  padding: 0;
}
.link-btn:hover { text-decoration: underline; }
</style>
