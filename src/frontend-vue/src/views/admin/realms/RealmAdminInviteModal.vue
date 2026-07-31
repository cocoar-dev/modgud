<script setup lang="ts">
import { computed, ref } from 'vue'
import { CoarButton, CoarFormField, CoarNotice, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useRealmStore } from '@/stores/realm.store'
import type { InitialAdminInviteDto } from '@/models/realm'

const props = defineProps<{
  slug: string
  close: () => void
}>()

const { t } = useI18n()
const store = useRealmStore()
const loading = ref(false)
const error = ref<string | null>(null)
const issuedInvite = ref<InitialAdminInviteDto | null>(null)
const linkCopied = ref(false)

const form = ref({
  UserName: '',
  Email: '',
  Firstname: '',
  Lastname: '',
})

const emailInvalid = computed(() => {
  const value = form.value.Email.trim()
  return value.length > 0 && !value.includes('@')
})

const canSubmit = computed(() =>
  !loading.value &&
  !!form.value.UserName.trim() &&
  !!form.value.Email.trim() &&
  !emailInvalid.value,
)

const footerButton = computed(() => issuedInvite.value
  ? {
      visible: true,
      text: t('common.close', {}, 'Schließen'),
      onClick: props.close,
    }
  : {
      visible: true,
      text: t('admin.realms.adminInvite.submit', {}, 'Einladung erstellen'),
      disabled: !canSubmit.value,
      loading: loading.value,
      onClick: submit,
    })

async function submit() {
  if (!canSubmit.value) return
  loading.value = true
  error.value = null
  try {
    issuedInvite.value = await store.issueAdminInvite(props.slug, {
      UserName: form.value.UserName.trim(),
      Email: form.value.Email.trim(),
      Firstname: form.value.Firstname.trim() || null,
      Lastname: form.value.Lastname.trim() || null,
    })
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
    // The visible read-only field remains the fallback.
  }
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="t('admin.realms.adminInvite.title', {}, 'Realm-Admin einladen')"
    :sub-title="slug"
    icon="mail"
    :footer-button="footerButton"
  >
    <div v-if="issuedInvite" class="flex min-w-0 flex-1 flex-col gap-3">
      <CoarNotice variant="success">
        {{ t('admin.realms.adminInvite.issued', {}, 'Die Einladung wurde erstellt. Wenn der E-Mail-Versand eingerichtet ist, wurde sie auch versendet.') }}
      </CoarNotice>
      <CoarNotice truncate variant="warning">
        {{ t('admin.realms.adminInvite.linkOnceShort', {}, 'Der Magic-Link wird nur jetzt angezeigt.') }}
        <template #details>
          {{ t('admin.realms.adminInvite.linkOnce', {}, 'Kopiere den Link jetzt, falls lokal kein E-Mail-Versand eingerichtet ist. Eine neue Einladung macht diesen Link sofort ungültig.') }}
        </template>
      </CoarNotice>

      <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
        <span class="text-gray-500">{{ t('admin.realms.adminInvite.userName', {}, 'Benutzername') }}</span>
        <span class="font-medium">{{ issuedInvite.UserName }}</span>
        <span class="text-gray-500">{{ t('admin.realms.adminInvite.email', {}, 'E-Mail') }}</span>
        <span>{{ issuedInvite.Email }}</span>
        <span class="text-gray-500">{{ t('admin.realms.adminInvite.expiresAt', {}, 'Gültig bis') }}</span>
        <span>{{ new Date(issuedInvite.ExpiresAt).toLocaleString() }}</span>
      </div>

      <CoarFormField :label="t('admin.realms.adminInvite.link', {}, 'Magic-Link')">
        <div class="flex gap-2">
          <input :value="issuedInvite.MagicLinkUrl" readonly class="invite-link" />
          <CoarButton @click="copyLink">
            {{ linkCopied ? t('common.copied', {}, 'Kopiert!') : t('common.copy', {}, 'Kopieren') }}
          </CoarButton>
        </div>
      </CoarFormField>
    </div>

    <div v-else class="modal-form">
      <CoarNotice variant="info">
        {{ t('admin.realms.adminInvite.singleActive', {}, 'Pro Realm kann nur eine Einladung aktiv sein. Eine neue Einladung widerruft den bisherigen Link und ist 24 Stunden gültig.') }}
      </CoarNotice>

      <section class="form-section">
        <div class="modal-form-grid">
          <CoarFormField class="col-half"
            :label="t('admin.realms.adminInvite.userName', {}, 'Benutzername')" required>
            <CoarTextInput v-model="form.UserName" clearable placeholder="admin" />
          </CoarFormField>
          <CoarFormField class="col-half"
            :label="t('admin.realms.adminInvite.email', {}, 'E-Mail')" required
            :error="emailInvalid ? t('admin.realms.adminInvite.emailInvalid', {}, 'Bitte eine gültige E-Mail-Adresse eingeben.') : undefined">
            <CoarTextInput v-model="form.Email" clearable placeholder="admin@example.com" />
          </CoarFormField>
          <CoarFormField class="col-half" :label="t('admin.realms.adminInvite.firstname', {}, 'Vorname')">
            <CoarTextInput v-model="form.Firstname" clearable />
          </CoarFormField>
          <CoarFormField class="col-half" :label="t('admin.realms.adminInvite.lastname', {}, 'Nachname')">
            <CoarTextInput v-model="form.Lastname" clearable />
          </CoarFormField>
        </div>
      </section>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.invite-link {
  min-width: 0;
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.75rem;
}
</style>
