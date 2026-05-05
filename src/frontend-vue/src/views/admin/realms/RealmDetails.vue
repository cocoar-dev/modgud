<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarNote, CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
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
  Domains: string  // newline
  IsControlPlane: boolean
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
    Domains: '',
    IsControlPlane: false,
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

function fromDto(dto: RealmDto): FormState {
  return {
    ...emptyForm(),
    Slug: dto.Slug,
    DisplayName: dto.DisplayName,
    Description: dto.Description ?? '',
    Domains: (dto.Domains ?? []).join('\n'),
    IsControlPlane: dto.IsControlPlane,
    IsActive: dto.IsActive,
  }
}

function splitLines(input: string): string[] {
  return input.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.realms.createTitle', {}, 'Realm erstellen')
    : (form.value.DisplayName || form.value.Slug)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Slug)

const canSubmit = computed(() => {
  if (loading.value) return false
  if (!form.value.DisplayName.trim()) return false
  if (isCreate.value) {
    if (!form.value.Slug.trim()) return false
    if (!form.value.InitialAdminUserName.trim()) return false
    if (!form.value.InitialAdminEmail.trim() || !form.value.InitialAdminEmail.includes('@')) return false
  }
  return true
})

const footerButton = computed(() => issuedInvite.value
  ? {
      visible: true,
      text: t('common.close', {}, 'Schließen'),
      onClick: () => props.close(),
    }
  : {
      visible: true,
      text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
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
      error.value = t('admin.realms.loadFailed', {}, 'Realm konnte nicht geladen werden.')
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
        Domains: splitLines(form.value.Domains),
        IsControlPlane: form.value.IsControlPlane,
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
        Domains: splitLines(form.value.Domains),
        IsControlPlane: form.value.IsControlPlane,
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
    :footer-button="footerButton" width="42rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>

    <!-- Invite-reveal screen — replaces the form after successful create/resend. -->
    <div v-else-if="issuedInvite" class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote variant="success">
        {{ inviteSource === 'resent'
            ? t('admin.realms.inviteResentTitle', {}, 'Bootstrap-Invite neu ausgestellt — alter Token wurde widerrufen.')
            : t('admin.realms.inviteIssuedTitle', {}, 'Realm angelegt — Bootstrap-Invite ausgestellt.') }}
      </CoarNote>
      <CoarNote variant="warning">
        {{ t('admin.realms.inviteIssuedHint', {}, 'Diese Magic-Link-URL wird genau einmal angezeigt. Falls die Email nicht zugestellt wird (z. B. lokale Entwicklung ohne SMTP), kopieren Sie sie jetzt — danach geht es nur noch über "Resend".') }}
      </CoarNote>
      <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <span class="text-gray-500">{{ t('admin.realms.inviteUserName', {}, 'Benutzername') }}</span>
        <span class="font-medium">{{ issuedInvite.UserName }}</span>
        <span class="text-gray-500">{{ t('admin.realms.inviteEmail', {}, 'E-Mail') }}</span>
        <span>{{ issuedInvite.Email }}</span>
        <span class="text-gray-500">{{ t('admin.realms.inviteExpiresAt', {}, 'Gültig bis') }}</span>
        <span>{{ new Date(issuedInvite.ExpiresAt).toLocaleString() }}</span>
      </div>
      <CoarFormField :label="t('admin.realms.inviteLink', {}, 'Magic-Link')">
        <div class="flex gap-2">
          <input :value="issuedInvite.MagicLinkUrl" readonly class="textarea !font-mono !text-xs" />
          <CoarButton @click="copyLink">
            {{ linkCopied ? t('common.copied', {}, 'Kopiert!') : t('common.copy', {}, 'Kopieren') }}
          </CoarButton>
        </div>
      </CoarFormField>
    </div>

    <!-- Edit/Create form -->
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.realms.createHint', {}, 'Beim Anlegen wird automatisch eine eigene Datenbank provisioniert und mit Default-OAuth-Scopes geseedet.') }}
      </CoarNote>

      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.realms.slug', {}, 'Slug (immutable)')">
          <CoarTextInput v-model="form.Slug" :disabled="!isCreate" clearable
            :placeholder="t('admin.realms.slugPlaceholder', {}, 'kebab-case-slug')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.realms.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
      </div>

      <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>

      <CoarFormField :label="t('admin.realms.domains', {}, 'Domains (eine pro Zeile)')">
        <textarea v-model="form.Domains" rows="3" class="textarea"
          placeholder="example.com&#10;auth.example.com" />
      </CoarFormField>

      <div class="flex flex-wrap gap-x-6 gap-y-2 mt-1">
        <CoarCheckbox v-model="form.IsControlPlane"
          :label="t('admin.realms.isControlPlane', {}, 'Control Plane (cross-realm Admin-Oberfläche)')" />
        <CoarCheckbox v-if="!isCreate" v-model="form.IsActive"
          :label="t('common.active', {}, 'Aktiv')" />
      </div>

      <!-- InitialAdmin (create-only) -->
      <div v-if="isCreate" class="mt-2 border-t pt-3 flex flex-col gap-2">
        <h4 class="text-sm font-medium text-gray-700">{{ t('admin.realms.initialAdminTitle', {}, 'Erster Admin') }}</h4>
        <p class="text-xs text-gray-500">
          {{ t('admin.realms.initialAdminHint', {}, 'Wird per Magic-Link zum Aktivieren eingeladen — der Empfänger setzt sein Passwort selbst. Pflichtfelder: Benutzername und E-Mail.') }}
        </p>
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.realms.initialAdminUserName', {}, 'Benutzername')">
            <CoarTextInput v-model="form.InitialAdminUserName" clearable placeholder="admin" />
          </CoarFormField>
          <CoarFormField :label="t('admin.realms.initialAdminEmail', {}, 'E-Mail')">
            <CoarTextInput v-model="form.InitialAdminEmail" clearable placeholder="admin@example.com" />
          </CoarFormField>
        </div>
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.realms.initialAdminFirstname', {}, 'Vorname (optional)')">
            <CoarTextInput v-model="form.InitialAdminFirstname" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.realms.initialAdminLastname', {}, 'Nachname (optional)')">
            <CoarTextInput v-model="form.InitialAdminLastname" clearable />
          </CoarFormField>
        </div>
      </div>

      <!-- Resend (edit-only) -->
      <div v-if="!isCreate" class="mt-2 border-t pt-3 flex items-center gap-3">
        <span class="text-xs text-gray-500 flex-1">
          {{ t('admin.realms.resendHint', {}, 'Bootstrap-Invite erneut ausstellen (z. B. wenn Token abgelaufen oder Email nie zugestellt).') }}
        </span>
        <CoarButton variant="secondary" :loading="loading" @click="resendInvite">
          {{ t('admin.realms.resendInvite', {}, 'Invite erneut senden') }}
        </CoarButton>
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
