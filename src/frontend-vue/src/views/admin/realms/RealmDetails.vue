<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarButton,
  CoarDivider,
  CoarIcon,
  CoarTab,
  CoarTabGroup,
  vTooltip,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import RealmDomainsField from '@/components/RealmDomainsField.vue'
import { useRealmStore } from '@/stores/realm.store'
import type { RealmDto } from '@/models/realm'

const { t } = useI18n()

// `id` from the routed modal carries the realm's Slug (the URL key for realms).
const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const slug = computed(() => props.id)
const store = useRealmStore()
const isCreate = computed(() => slug.value === 'create')
const loading = ref(false)
const error = ref<string | null>(null)
const activeTab = ref<'general' | 'domains'>('general')

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
  Domains: string[]
  PrimaryDomain: string
  IsActive: boolean
}

function emptyForm(): FormState {
  return {
    Slug: '',
    DisplayName: '',
    Description: '',
    Domains: [],
    PrimaryDomain: '',
    IsActive: true,
  }
}

const form = ref<FormState>(emptyForm())
const dto = ref<RealmDto | null>(null)

// Control-plane transfer: terminal state after a successful move (the current
// host loses the realm-management surface, so we don't return to the form).
const transferring = ref(false)
const transferResult = ref<RealmDto | null>(null)

function fromDto(dto: RealmDto): FormState {
  return {
    ...emptyForm(),
    Slug: dto.Slug,
    DisplayName: dto.DisplayName,
    Description: dto.Description ?? '',
    Domains: [...(dto.Domains ?? [])],
    PrimaryDomain: dto.PrimaryDomain ?? '',
    IsActive: dto.IsActive,
  }
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.realms.createTitle', {}, 'Create Realm')
    : (form.value.DisplayName || form.value.Slug)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Slug)

const REALM_SLUG_REGEX = /^[a-z][a-z0-9-]{1,61}[a-z0-9]$/
const slugError = computed(() => isCreate.value && !REALM_SLUG_REGEX.test(form.value.Slug.trim())
  ? t('admin.realms.validation.slug', {}, 'Enter a valid slug (3–63 lowercase letters, digits or hyphens).')
  : '')
const displayNameError = computed(() => !form.value.DisplayName.trim()
  ? t('admin.realms.validation.displayName', {}, 'Display name is required.')
  : '')
const domainsError = computed(() => form.value.Domains.length < 1
  ? t('admin.realms.validation.domains', {}, 'Add at least one domain.')
  : '')

const canSubmit = computed(() => {
  if (loading.value) return false
  if (displayNameError.value || domainsError.value) return false
  if (isCreate.value && slugError.value) return false
  return true
})

// Edit-only: the admin picked a different primary than the realm currently has.
// Changing it re-keys the WebAuthn RP, so existing passkeys stop working.
const primaryChanged = computed(() =>
  !isCreate.value &&
  !!dto.value &&
  !!form.value.PrimaryDomain &&
  form.value.PrimaryDomain !== dto.value.PrimaryDomain,
)

const footerButton = computed(() => transferResult.value
  ? {
      visible: true,
      text: t('common.close', {}, 'Close'),
      onClick: () => props.close(),
    }
  : {
      visible: true,
      text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
      disabled: !canSubmit.value,
      loading: loading.value,
      onClick: save,
    })

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const loaded = await store.loadOne(slug.value)
    if (!loaded) {
      error.value = t('admin.realms.loadFailed', {}, 'Failed to load the realm.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
  } finally {
    loading.value = false
  }
})

async function save() {
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      await store.create({
        Slug: form.value.Slug.trim(),
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Domains: [...form.value.Domains],
        PrimaryDomain: form.value.PrimaryDomain.trim() || null,
        IsActive: form.value.IsActive,
      })
      props.close()
    } else {
      await store.update(slug.value, {
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Domains: [...form.value.Domains],
        PrimaryDomain: form.value.PrimaryDomain.trim() || null,
        IsActive: form.value.IsActive,
      })
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function transferControlPlane() {
  const target = dto.value
  if (!target || target.IsControlPlane || !target.IsActive) return
  const confirmMsg = t(
    'admin.realms.confirmTransferControlPlane',
    { slug: target.Slug },
    `Make "${target.Slug}" the control plane? Cross-realm administration moves to that realm and this current host loses the realm-management surface.`,
  )
  if (!confirm(confirmMsg)) return

  transferring.value = true
  error.value = null
  try {
    transferResult.value = await store.transferControlPlane(target.Slug)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    transferring.value = false
  }
}

</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="globe"
    :footer-button="footerButton">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <!-- Control-plane transfer result — terminal state (this host is no longer the CP). -->
    <div v-else-if="transferResult" class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNotice variant="success">
        {{ t('admin.realms.transferDoneTitle', { slug: transferResult.Slug }, `Control plane moved to "${transferResult.Slug}".`) }}
      </CoarNotice>
      <CoarNotice truncate variant="warning">
        {{ t('admin.realms.transferDoneHintShort', {}, 'Control-plane administration now lives on the target realm\'s domain(s).') }}
        <template #details>
          {{ t('admin.realms.transferDoneHint', {}, 'This host is no longer the control plane — realm management now lives on the target realm domain(s) below. Continue administration there.') }}
        </template>
      </CoarNotice>
      <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <span class="text-gray-500">{{ t('admin.realms.displayName', {}, 'Display Name') }}</span>
        <span class="font-medium">{{ transferResult.DisplayName }}</span>
        <span class="text-gray-500">{{ t('admin.realms.domains', {}, 'Domains') }}</span>
        <span>{{ (transferResult.Domains ?? []).join(', ') }}</span>
        <span class="text-gray-500">{{ t('admin.realms.primaryDomain', {}, 'Primary domain') }}</span>
        <span class="font-medium">{{ transferResult.PrimaryDomain }}</span>
      </div>
    </div>

    <!-- Edit/Create form -->
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNotice truncate v-if="!isCreate && dto?.IsControlPlane" variant="info">
        {{ t('admin.realms.isControlPlaneNoteShort', {}, 'This realm hosts cross-realm administration as the control plane.') }}
        <template #details>
          {{ t('admin.realms.isControlPlaneNote', {}, 'This realm is the control plane — it hosts cross-realm administration. To move the role, open the target realm and make it the control plane.') }}
        </template>
      </CoarNotice>

      <CoarNotice v-if="isCreate" variant="info">
        {{ t('admin.realms.createHint', {}, 'Creating it automatically provisions a dedicated database and seeds it with the default OAuth scopes.') }}
      </CoarNotice>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.realms.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab
          id="domains"
          v-tooltip="{
            content: t('admin.realms.tabs.domainsHint', {}, 'At least one domain is required. The primary domain is used for links in e-mails and for passkeys.'),
            placement: 'top',
          }"
        >
          <span class="domains-tab-label">
            {{ t('admin.realms.tabs.domains', {}, 'Domains') }}
            <span class="domains-tab-info" aria-hidden="true">
              <CoarIcon name="info" size="s" aria-hidden="true" />
            </span>
          </span>
        </CoarTab>
      </CoarTabGroup>

      <!-- Tab: General -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.realms.section.identity', {}, 'Identity') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.realms.slug', {}, 'Slug')" required
              :error="slugError"
              :hint="t('admin.realms.slug.hint', {}, 'Permanent URL / API identifier in kebab-case. Immutable after creation.')">
              <CoarTextInput v-model="form.Slug" :disabled="!isCreate" clearable
                :placeholder="t('admin.realms.slugPlaceholder', {}, 'kebab-case-slug')" />
            </CoarFormField>

            <CoarFormField class="col-half realm-active-field" layout="inline" label-position="after"
              :label="t('common.active', {}, 'Active')"
              :hint="dto?.IsControlPlane
                ? t('admin.realms.isActive.controlPlaneHint', {}, 'The Control-Plane realm cannot be deactivated.')
                : t('admin.realms.isActive.hint', {}, 'Inactive realms cannot sign in and cannot become the control plane.')">
              <CoarCheckbox v-model="form.IsActive" :disabled="dto?.IsControlPlane" />
            </CoarFormField>

            <CoarFormField class="col-full" :label="t('admin.realms.displayName', {}, 'Display name')" required
              :error="displayNameError"
              :hint="t('admin.realms.displayName.hint', {}, 'Human-friendly name shown in the realm switcher and headers.')">
              <CoarTextInput v-model="form.DisplayName" clearable />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('common.description', {}, 'Description')"
              :hint="t('admin.realms.description.hint', {}, 'Optional note describing this realm\'s purpose.')">
              <CoarTextInput v-model="form.Description" clearable :rows="2" />
            </CoarFormField>
          </div>
        </section>

        <!-- Control plane transfer (edit-only, non-Control-Plane realms). -->
        <section v-if="!isCreate && dto && !dto.IsControlPlane" class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.realms.controlPlaneTitle', {}, 'Control Plane') }}</h3>
          </CoarDivider>
          <p class="text-xs text-gray-500">
            {{ t('admin.realms.transferControlPlaneHint', {}, 'Make this realm the control plane. Cross-realm administration moves here and the current host loses the realm-management surface. The target realm admins (realm:admin) gain it automatically.') }}
          </p>
          <div>
            <CoarButton variant="danger" :loading="transferring" :disabled="!dto.IsActive"
              @click="transferControlPlane">
              {{ t('admin.realms.transferControlPlane', {}, 'Make this realm the control plane') }}
            </CoarButton>
          </div>
        </section>
      </div>

      <!-- Tab: Domains -->
      <div v-show="activeTab === 'domains'" class="tab-content domains-tab-content">
        <RealmDomainsField
          v-model:domains="form.Domains"
          v-model:primary="form.PrimaryDomain"
          :placeholder="t('admin.realms.domain.placeholder', {}, 'auth.example.com')" />
        <p v-if="domainsError" class="domains-error">{{ domainsError }}</p>
        <CoarNotice truncate v-if="primaryChanged" variant="warning">
          {{ t('admin.realms.primaryChangedWarningShort', {}, 'Changing the primary domain invalidates existing passkeys for this realm.') }}
          <template #details>
            {{ t('admin.realms.primaryChangedWarning', {}, 'Changing the primary domain invalidates this realm\'s existing passkeys — they are bound to the previous host. Affected users must re-register their passkeys on the new primary domain.') }}
          </template>
        </CoarNotice>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.realm-active-field {
  align-self: start;
  padding-top: 1.65rem;
}

.tab-bar {
  margin-bottom: 12px;
}

.domains-tab-label,
.domains-tab-info {
  display: inline-flex;
  align-items: center;
}

.domains-tab-label {
  gap: 0.35rem;
}

.domains-tab-info {
  width: 1rem;
  height: 1rem;
  color: var(--coar-text-neutral-tertiary, #6b7280);
}

.tab-content {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.domains-tab-content {
  flex: 1;
}

.domains-error {
  margin: -4px 0 0;
  color: var(--coar-text-semantic-danger, #dc2626);
  font-size: 0.75rem;
}
</style>
