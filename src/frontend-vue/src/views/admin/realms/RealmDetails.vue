<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarNote, CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import RealmDomainsField from '@/components/RealmDomainsField.vue'
import { useRealmStore } from '@/stores/realm.store'
import type { RealmDto, InitialAdminInviteDto } from '@/models/realm'

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

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
  Domains: string[]
  PrimaryDomain: string
  IsActive: boolean
  InitialAdminUserName: string
  InitialAdminEmail: string
  InitialAdminFirstname: string
  InitialAdminLastname: string
}

function emptyForm(): FormState {
  return {
    Slug: '',
    DisplayName: '',
    Description: '',
    Domains: [],
    PrimaryDomain: '',
    IsActive: true,
    InitialAdminUserName: '',
    InitialAdminEmail: '',
    InitialAdminFirstname: '',
    InitialAdminLastname: '',
  }
}

const form = ref<FormState>(emptyForm())
const dto = ref<RealmDto | null>(null)

// One-shot invite reveal after successful creation / resend. Cleared
// when modal closes; the user has to copy the link before then if there
// is no SMTP delivery (dev / air-gapped).
const issuedInvite = ref<InitialAdminInviteDto | null>(null)
const inviteSource = ref<'created' | 'resent' | null>(null)
const linkCopied = ref(false)

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

// Visible inline email validation for the InitialAdmin field — mirrors the
// silent gate that used to live in canSubmit, but now surfaces a message so
// the user knows WHY the button is disabled (no type=email — CoarTextInput
// has no type prop). Empty is not "invalid" (the required marker covers that);
// only a non-empty, malformed value lights up red.
const initialAdminEmailInvalid = computed(() => {
  const v = form.value.InitialAdminEmail.trim()
  return v.length > 0 && !v.includes('@')
})

const canSubmit = computed(() => {
  if (loading.value) return false
  if (!form.value.DisplayName.trim()) return false
  // A realm must have at least one domain — it can't route or build outbound
  // links otherwise. The backend rejects empty domains on both create and
  // update (update can't clear them), so gate the button here too.
  if (form.value.Domains.length < 1) return false
  if (isCreate.value) {
    if (!form.value.Slug.trim()) return false
    if (!form.value.InitialAdminUserName.trim()) return false
    if (!form.value.InitialAdminEmail.trim() || initialAdminEmailInvalid.value) return false
  }
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

const footerButton = computed(() => (issuedInvite.value || transferResult.value)
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
      const result = await store.create({
        Slug: form.value.Slug.trim(),
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Domains: [...form.value.Domains],
        PrimaryDomain: form.value.PrimaryDomain.trim() || null,
        InitialAdmin: {
          UserName: form.value.InitialAdminUserName.trim(),
          Email: form.value.InitialAdminEmail.trim(),
          Firstname: form.value.InitialAdminFirstname.trim() || null,
          Lastname: form.value.InitialAdminLastname.trim() || null,
        },
      })
      // Show the bootstrap-invite reveal screen — the magic-link is
      // only available right after creation; closing the modal loses it.
      issuedInvite.value = result.InitialAdminInvite
      inviteSource.value = 'created'
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

async function resendInvite() {
  loading.value = true
  error.value = null
  try {
    const reissued = await store.resendBootstrapInvite(slug.value)
    issuedInvite.value = reissued
    inviteSource.value = 'resent'
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

async function copyLink() {
  if (!issuedInvite.value) return
  try {
    await navigator.clipboard.writeText(issuedInvite.value.MagicLinkUrl)
    linkCopied.value = true
    setTimeout(() => { linkCopied.value = false }, 1800)
  } catch {
    /* ignore — fallback is the visible link below */
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="globe"
    :footer-button="footerButton">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <!-- Invite-reveal screen — replaces the form after successful create/resend. -->
    <div v-else-if="issuedInvite" class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote variant="success">
        {{ inviteSource === 'resent'
            ? t('admin.realms.inviteResentTitle', {}, 'Bootstrap invite reissued — the old token has been revoked.')
            : t('admin.realms.inviteIssuedTitle', {}, 'Realm created — bootstrap invite issued.') }}
      </CoarNote>
      <CoarNote variant="warning">
        {{ t('admin.realms.inviteIssuedHint', {}, 'This magic link URL is shown exactly once. If the email isn\'t delivered (e.g. local development without SMTP), copy it now — afterwards it\'s only available via "Resend".') }}
      </CoarNote>
      <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <span class="text-gray-500">{{ t('admin.realms.inviteUserName', {}, 'Benutzername') }}</span>
        <span class="font-medium">{{ issuedInvite.UserName }}</span>
        <span class="text-gray-500">{{ t('admin.realms.inviteEmail', {}, 'E-Mail') }}</span>
        <span>{{ issuedInvite.Email }}</span>
        <span class="text-gray-500">{{ t('admin.realms.inviteExpiresAt', {}, 'Valid until') }}</span>
        <span>{{ new Date(issuedInvite.ExpiresAt).toLocaleString() }}</span>
      </div>
      <CoarFormField :label="t('admin.realms.inviteLink', {}, 'Magic-Link')">
        <div class="flex gap-2">
          <input :value="issuedInvite.MagicLinkUrl" readonly class="textarea !font-mono !text-xs" />
          <CoarButton @click="copyLink">
            {{ linkCopied ? t('common.copied', {}, 'Kopiert!') : t('common.copy', {}, 'Copy') }}
          </CoarButton>
        </div>
      </CoarFormField>
    </div>

    <!-- Control-plane transfer result — terminal state (this host is no longer the CP). -->
    <div v-else-if="transferResult" class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote variant="success">
        {{ t('admin.realms.transferDoneTitle', { slug: transferResult.Slug }, `Control plane moved to "${transferResult.Slug}".`) }}
      </CoarNote>
      <CoarNote variant="warning">
        {{ t('admin.realms.transferDoneHint', {}, 'This host is no longer the control plane — realm management now lives on the target realm domain(s) below. Continue administration there.') }}
      </CoarNote>
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
      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.realms.createHint', {}, 'Creating it automatically provisions a dedicated database and seeds it with the default OAuth scopes.') }}
      </CoarNote>

      <div class="modal-form">
        <!-- Section: Identity -->
        <section class="form-section">
          <h3 class="form-section-heading">{{ t('admin.realms.section.identity', {}, 'Identity') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.realms.slug', {}, 'Slug')" required>
              <CoarTextInput v-model="form.Slug" :disabled="!isCreate" clearable
                :placeholder="t('admin.realms.slugPlaceholder', {}, 'kebab-case-slug')" />
              <p class="field-hint">{{ t('admin.realms.slug.hint', {}, 'Permanent URL / API identifier in kebab-case. Immutable after creation.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.realms.displayName', {}, 'Display name')" required>
              <CoarTextInput v-model="form.DisplayName" clearable />
              <p class="field-hint">{{ t('admin.realms.displayName.hint', {}, 'Human-friendly name shown in the realm switcher and headers.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('common.description', {}, 'Description')">
              <CoarTextInput v-model="form.Description" clearable :rows="2" />
              <p class="field-hint">{{ t('admin.realms.description.hint', {}, 'Optional note describing this realm\'s purpose.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.realms.domains', {}, 'Domains')" required>
              <RealmDomainsField
                v-model:domains="form.Domains"
                v-model:primary="form.PrimaryDomain"
                :placeholder="t('admin.realms.domain.placeholder', {}, 'auth.example.com')" />
              <p class="field-hint">
                {{ t('admin.realms.primaryDomainHint', {}, 'The realm routes on any domain, but the one marked Primary is its canonical public host: all invite / magic-link / reset mails use it, and passkeys (WebAuthn) only work on it.') }}
              </p>
              <CoarNote v-if="primaryChanged" variant="warning" class="mt-2">
                {{ t('admin.realms.primaryChangedWarning', {}, 'Changing the primary domain invalidates this realm\'s existing passkeys — they are bound to the previous host. Affected users must re-register their passkeys on the new primary domain.') }}
              </CoarNote>
            </CoarFormField>
          </div>
        </section>

        <!-- Section: InitialAdmin (create-only) -->
        <section v-if="isCreate" class="form-section">
          <h3 class="form-section-heading">{{ t('admin.realms.initialAdminTitle', {}, 'First Admin') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-full">
              <p class="field-hint">
                {{ t('admin.realms.initialAdminHint', {}, 'Invited via magic link to activate — the recipient sets their own password. Required fields: username and email.') }}
              </p>
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.realms.initialAdminUserName', {}, 'Benutzername')" required>
              <CoarTextInput v-model="form.InitialAdminUserName" clearable placeholder="admin" />
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.realms.initialAdminEmail', {}, 'E-Mail')" required>
              <CoarTextInput v-model="form.InitialAdminEmail" clearable placeholder="admin@example.com" />
              <p v-if="initialAdminEmailInvalid" class="text-sm text-red-600">
                {{ t('admin.realms.initialAdminEmailInvalid', {}, 'Please enter a valid email address.') }}
              </p>
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.realms.initialAdminFirstname', {}, 'Vorname')">
              <CoarTextInput v-model="form.InitialAdminFirstname" clearable />
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.realms.initialAdminLastname', {}, 'Nachname')">
              <CoarTextInput v-model="form.InitialAdminLastname" clearable />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Status (edit-only) -->
        <section v-if="!isCreate" class="form-section">
          <h3 class="form-section-heading">{{ t('admin.realms.section.status', {}, 'Status') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-full">
              <CoarCheckbox v-model="form.IsActive" :label="t('common.active', {}, 'Active')" />
              <p class="field-hint">{{ t('admin.realms.isActive.hint', {}, 'Inactive realms cannot sign in and cannot become the control plane.') }}</p>
            </CoarFormField>
          </div>
        </section>
      </div>

      <!-- Resend (edit-only) -->
      <div v-if="!isCreate" class="mt-2 border-t pt-3 flex items-center gap-3">
        <span class="text-xs text-gray-500 flex-1">
          {{ t('admin.realms.resendHint', {}, 'Reissue the bootstrap invite (e.g. if the token expired or the email was never delivered).') }}
        </span>
        <CoarButton variant="secondary" :loading="loading" @click="resendInvite">
          {{ t('admin.realms.resendInvite', {}, 'Resend Invite') }}
        </CoarButton>
      </div>

      <!-- Control plane (edit-only) -->
      <div v-if="!isCreate && dto" class="mt-2 border-t pt-3 flex flex-col gap-2">
        <h4 class="text-sm font-medium text-gray-700">
          {{ t('admin.realms.controlPlaneTitle', {}, 'Control Plane') }}
        </h4>

        <CoarNote v-if="dto.IsControlPlane" variant="info">
          {{ t('admin.realms.isControlPlaneNote', {}, 'This realm is the control plane — it hosts cross-realm administration. To move the role, open the target realm and make it the control plane.') }}
        </CoarNote>

        <template v-else>
          <p class="text-xs text-gray-500">
            {{ t('admin.realms.transferControlPlaneHint', {}, 'Make this realm the control plane. Cross-realm administration moves here and the current host loses the realm-management surface. The target realm admins (realm:admin) gain it automatically.') }}
          </p>
          <div>
            <CoarButton variant="danger" :loading="transferring" :disabled="!dto.IsActive"
              @click="transferControlPlane">
              {{ t('admin.realms.transferControlPlane', {}, 'Make this realm the control plane') }}
            </CoarButton>
          </div>
        </template>
      </div>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.textarea {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.8rem;
  resize: vertical;
}
</style>
