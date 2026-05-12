<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  CoarCard,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
  CoarButton,
  CoarMultiSelect,
  CoarTabGroup,
  CoarTab,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import EditableStringList from '@/components/EditableStringList.vue'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useGroupStore } from '@/stores/group.store'
import type { SelfRegistrationDto, UpdateSelfRegistrationDto } from '@/models/realmSettings'

const { t, language } = useI18n()
const ui = useUI()
const settingsStore = useRealmSettingsStore()
const groupStore = useGroupStore()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.realmSettings.title', {}, 'Realm settings')
  ctx.header.icon = 'sliders'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

type TabId = 'self-registration'
const activeTab = ref<TabId>('self-registration')

// ── Self-Registration form state ─────────────────────────────────────
interface SelfRegFormState {
  Enabled: boolean
  RequireEmailVerification: boolean
  RequireAdminApproval: boolean
  AllowedEmailDomains: string[]
  DefaultGroupIds: string[]
  TermsOfServiceUrl: string
  PrivacyPolicyUrl: string
  CaptchaEnabled: boolean
  CaptchaSiteKey: string
  CaptchaSecretSet: boolean
}

function emptySelfReg(): SelfRegFormState {
  return {
    Enabled: false,
    RequireEmailVerification: true,
    RequireAdminApproval: false,
    AllowedEmailDomains: [],
    DefaultGroupIds: [],
    TermsOfServiceUrl: '',
    PrivacyPolicyUrl: '',
    CaptchaEnabled: false,
    CaptchaSiteKey: '',
    CaptchaSecretSet: false,
  }
}

const form = ref<SelfRegFormState>(emptySelfReg())
const originalSelfReg = ref<SelfRegistrationDto | null>(null)

// Captcha-secret — write-only with three states: leave, clear, replace.
const editingSecret = ref(false)
const secretInput = ref('')

const groupOptions = computed(() =>
  groupStore.groups.map((g) => ({ value: g.Id, label: g.Name })))

const initialLoad = ref(true)
const saving = ref(false)
const error = ref<string | null>(null)
const savedFlash = ref(false)

function fromDto(sr: SelfRegistrationDto): SelfRegFormState {
  return {
    Enabled: sr.Enabled,
    RequireEmailVerification: sr.RequireEmailVerification,
    RequireAdminApproval: sr.RequireAdminApproval,
    AllowedEmailDomains: [...(sr.AllowedEmailDomains ?? [])],
    DefaultGroupIds: [...(sr.DefaultGroupIds ?? [])],
    TermsOfServiceUrl: sr.TermsOfServiceUrl ?? '',
    PrivacyPolicyUrl: sr.PrivacyPolicyUrl ?? '',
    CaptchaEnabled: sr.CaptchaEnabled,
    CaptchaSiteKey: sr.CaptchaSiteKey ?? '',
    CaptchaSecretSet: sr.CaptchaSecretSet,
  }
}

onMounted(async () => {
  initialLoad.value = true
  try {
    const [dto] = await Promise.all([settingsStore.load(), groupStore.initialize()])
    originalSelfReg.value = dto.SelfRegistration
    form.value = fromDto(dto.SelfRegistration)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.message ?? String(e)
  } finally {
    initialLoad.value = false
  }
})

function buildSelfRegPatch(): UpdateSelfRegistrationDto | undefined {
  const orig = originalSelfReg.value
  if (!orig) return undefined
  const cur = form.value
  const patch: UpdateSelfRegistrationDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.RequireEmailVerification !== orig.RequireEmailVerification)
    patch.RequireEmailVerification = cur.RequireEmailVerification
  if (cur.RequireAdminApproval !== orig.RequireAdminApproval)
    patch.RequireAdminApproval = cur.RequireAdminApproval

  if (!arrayEqual(cur.AllowedEmailDomains, orig.AllowedEmailDomains ?? []))
    patch.AllowedEmailDomains = cur.AllowedEmailDomains.length ? cur.AllowedEmailDomains : null
  if (!arrayEqual(cur.DefaultGroupIds, orig.DefaultGroupIds ?? []))
    patch.DefaultGroupIds = cur.DefaultGroupIds.length ? cur.DefaultGroupIds : null

  const tos = cur.TermsOfServiceUrl.trim()
  if (tos !== (orig.TermsOfServiceUrl ?? '')) patch.TermsOfServiceUrl = tos || null
  const pp = cur.PrivacyPolicyUrl.trim()
  if (pp !== (orig.PrivacyPolicyUrl ?? '')) patch.PrivacyPolicyUrl = pp || null

  if (cur.CaptchaEnabled !== orig.CaptchaEnabled) patch.CaptchaEnabled = cur.CaptchaEnabled
  const key = cur.CaptchaSiteKey.trim()
  if (key !== (orig.CaptchaSiteKey ?? '')) patch.CaptchaSiteKey = key || null

  if (editingSecret.value) patch.CaptchaSecret = secretInput.value

  return Object.keys(patch).length === 0 ? undefined : patch
}

function arrayEqual(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false
  return true
}

async function save() {
  const selfRegPatch = buildSelfRegPatch()
  if (!selfRegPatch) {
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1200)
    return
  }
  saving.value = true
  error.value = null
  try {
    const updated = await settingsStore.patch({ SelfRegistration: selfRegPatch })
    originalSelfReg.value = updated.SelfRegistration
    form.value = fromDto(updated.SelfRegistration)
    editingSecret.value = false
    secretInput.value = ''
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4 gap-3">
    <CoarTabGroup v-model="activeTab" class="tab-bar">
      <CoarTab id="self-registration">
        {{ t('admin.realmSettings.tabs.selfRegistration', {}, 'Self-Registration') }}
      </CoarTab>
    </CoarTabGroup>

    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    <CoarNote v-if="savedFlash" variant="success">
      {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
    </CoarNote>

    <div v-if="initialLoad" class="text-sm text-gray-400">
      {{ t('common.loading', {}, 'Loading...') }}
    </div>

    <CoarCard v-else-if="activeTab === 'self-registration'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.selfReg.hint', {}, 'When enabled, visitors can create an account themselves at /register. Default: disabled.') }}
        </p>

        <CoarCheckbox
          v-model="form.Enabled"
          :label="t('admin.realmSettings.selfReg.enabled', {}, 'Enable self-registration')" />

        <template v-if="form.Enabled">
          <div class="flex flex-wrap gap-x-6 gap-y-2">
            <CoarCheckbox
              v-model="form.RequireEmailVerification"
              :label="t('admin.realmSettings.selfReg.requireEmailVerification', {}, 'Require email verification')" />
            <CoarCheckbox
              v-model="form.RequireAdminApproval"
              :label="t('admin.realmSettings.selfReg.requireAdminApproval', {}, 'Require admin approval')" />
          </div>

          <CoarFormField :label="t('admin.realmSettings.selfReg.allowedDomains', {}, 'Allowed email domains (empty = all)')">
            <EditableStringList
              v-model="form.AllowedEmailDomains"
              :placeholder="t('admin.realmSettings.selfReg.allowedDomains.placeholder', {}, 'example.com')" />
          </CoarFormField>

          <CoarFormField :label="t('admin.realmSettings.selfReg.defaultGroups', {}, 'Default groups (auto-membership after verification)')">
            <CoarMultiSelect
              v-model="form.DefaultGroupIds"
              :options="groupOptions"
              searchable
              clearable
              :placeholder="t('admin.realmSettings.selfReg.defaultGroups.placeholder', {}, 'Select groups…')" />
          </CoarFormField>

          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.selfReg.tosUrl', {}, 'Terms-of-Service URL (shows required checkbox)')">
              <CoarTextInput v-model="form.TermsOfServiceUrl" clearable placeholder="https://…" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.selfReg.privacyUrl', {}, 'Privacy Policy URL (footer link)')">
              <CoarTextInput v-model="form.PrivacyPolicyUrl" clearable placeholder="https://…" />
            </CoarFormField>
          </div>

          <CoarCheckbox
            v-model="form.CaptchaEnabled"
            :label="t('admin.realmSettings.selfReg.captchaEnabled', {}, 'Enable Cloudflare Turnstile captcha')" />

          <template v-if="form.CaptchaEnabled">
            <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSiteKey', {}, 'Captcha site key (empty = Cocoar default)')">
              <CoarTextInput v-model="form.CaptchaSiteKey" clearable
                :placeholder="t('admin.realmSettings.selfReg.captchaSiteKey.placeholder', {}, 'Site key (public)')" />
            </CoarFormField>

            <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSecret', {}, 'Captcha secret')">
              <div v-if="!editingSecret" class="flex items-center gap-2">
                <span class="text-sm text-gray-600">
                  <template v-if="form.CaptchaSecretSet">
                    ••••• {{ t('admin.realmSettings.selfReg.captchaSecret.set', {}, '(set — overrides default)') }}
                  </template>
                  <template v-else>
                    {{ t('admin.realmSettings.selfReg.captchaSecret.unset', {}, '(not set — uses Cocoar default)') }}
                  </template>
                </span>
                <CoarButton size="s" variant="secondary" @click="() => { editingSecret = true; secretInput = '' }">
                  {{ t('admin.realmSettings.selfReg.captchaSecret.replace', {}, 'Replace') }}
                </CoarButton>
              </div>
              <div v-else class="flex flex-col gap-1">
                <div class="flex items-center gap-2">
                  <CoarTextInput v-model="secretInput" clearable
                    :placeholder="t('admin.realmSettings.selfReg.captchaSecret.inputPlaceholder', {}, 'New secret (empty = clear/default)')" />
                  <CoarButton size="s" variant="secondary"
                    @click="() => { editingSecret = false; secretInput = '' }">
                    {{ t('common.cancel', {}, 'Cancel') }}
                  </CoarButton>
                </div>
                <p class="text-xs text-gray-500">
                  {{ t('admin.realmSettings.selfReg.captchaSecret.help', {}, 'Applied on save. Empty + save = clear secret (revert to Cocoar default).') }}
                </p>
              </div>
            </CoarFormField>
          </template>
        </template>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.tab-bar {
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
}
</style>
