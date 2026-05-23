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
import type {
  SelfRegistrationDto,
  UpdateSelfRegistrationDto,
  DcrSettingsDto,
  UpdateDcrSettingsDto,
} from '@/models/realmSettings'

const { t, language } = useI18n()
const ui = useUI()
const settingsStore = useRealmSettingsStore()
const groupStore = useGroupStore()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.realmSettings.title', {}, 'Realm settings')
  ctx.header.icon = 'sliders-horizontal'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

type TabId = 'self-registration' | 'dcr'
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

// ── DCR form state ───────────────────────────────────────────────────
interface DcrFormState {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
  GcTtlDays: number
  PerIpRateLimitPerHour: number
  PerRealmRateLimitPerDay: number
  ReservedNames: string[]
}

function emptyDcr(): DcrFormState {
  return {
    Enabled: false,
    AccessTokenLifetimeMinutes: 15,
    RefreshTokenLifetimeDays: 7,
    GcTtlDays: 90,
    PerIpRateLimitPerHour: 5,
    PerRealmRateLimitPerDay: 100,
    ReservedNames: [],
  }
}

const dcrForm = ref<DcrFormState>(emptyDcr())
const originalDcr = ref<DcrSettingsDto | null>(null)

function dcrFromDto(d: DcrSettingsDto): DcrFormState {
  return {
    Enabled: d.Enabled,
    AccessTokenLifetimeMinutes: d.AccessTokenLifetimeMinutes,
    RefreshTokenLifetimeDays: d.RefreshTokenLifetimeDays,
    GcTtlDays: d.GcTtlDays,
    PerIpRateLimitPerHour: d.PerIpRateLimitPerHour,
    PerRealmRateLimitPerDay: d.PerRealmRateLimitPerDay,
    ReservedNames: [...(d.ReservedNames ?? [])],
  }
}


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
    originalDcr.value = dto.Dcr
    dcrForm.value = dcrFromDto(dto.Dcr)
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

function buildDcrPatch(): UpdateDcrSettingsDto | undefined {
  const orig = originalDcr.value
  if (!orig) return undefined
  const cur = dcrForm.value
  const patch: UpdateDcrSettingsDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.AccessTokenLifetimeMinutes !== orig.AccessTokenLifetimeMinutes)
    patch.AccessTokenLifetimeMinutes = cur.AccessTokenLifetimeMinutes
  if (cur.RefreshTokenLifetimeDays !== orig.RefreshTokenLifetimeDays)
    patch.RefreshTokenLifetimeDays = cur.RefreshTokenLifetimeDays
  if (cur.GcTtlDays !== orig.GcTtlDays) patch.GcTtlDays = cur.GcTtlDays
  if (cur.PerIpRateLimitPerHour !== orig.PerIpRateLimitPerHour)
    patch.PerIpRateLimitPerHour = cur.PerIpRateLimitPerHour
  if (cur.PerRealmRateLimitPerDay !== orig.PerRealmRateLimitPerDay)
    patch.PerRealmRateLimitPerDay = cur.PerRealmRateLimitPerDay
  if (!arrayEqual(cur.ReservedNames, orig.ReservedNames ?? []))
    patch.ReservedNames = cur.ReservedNames.length ? cur.ReservedNames : null

  return Object.keys(patch).length === 0 ? undefined : patch
}

async function save() {
  const selfRegPatch = buildSelfRegPatch()
  const dcrPatch = buildDcrPatch()
  if (!selfRegPatch && !dcrPatch) {
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1200)
    return
  }
  saving.value = true
  error.value = null
  try {
    const payload: {
      SelfRegistration?: UpdateSelfRegistrationDto
      Dcr?: UpdateDcrSettingsDto
    } = {}
    if (selfRegPatch) payload.SelfRegistration = selfRegPatch
    if (dcrPatch) payload.Dcr = dcrPatch
    const updated = await settingsStore.patch(payload)
    originalSelfReg.value = updated.SelfRegistration
    form.value = fromDto(updated.SelfRegistration)
    originalDcr.value = updated.Dcr
    dcrForm.value = dcrFromDto(updated.Dcr)
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
      <CoarTab id="dcr">
        {{ t('admin.realmSettings.tabs.dcr', {}, 'Dynamic Client Registration') }}
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

    <CoarCard v-else-if="activeTab === 'dcr'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.dcr.hint', {}, 'When enabled, AI agents and other software can register OAuth clients themselves at POST /connect/register (RFC 7591). Public PKCE clients only, no client_secret issued. Off by default.') }}
        </p>

        <CoarCheckbox
          v-model="dcrForm.Enabled"
          :label="t('admin.realmSettings.dcr.enabled', {}, 'Enable Dynamic Client Registration')" />

        <CoarNote v-if="dcrForm.Enabled" variant="info">
          {{ t('admin.realmSettings.dcr.tripleOptInWarning', {}, 'Triple opt-in: clients registered here can only request access tokens for OAuth APIs with AllowDynamicRegistration enabled AND scopes with AllowDynamicRegistrationClients enabled. Until you opt in at least one API and one scope, DCR clients cannot mint usable tokens.') }}
        </CoarNote>

        <template v-if="dcrForm.Enabled">
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.dcr.accessTokenMinutes', {}, 'Access token lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(dcrForm.AccessTokenLifetimeMinutes)"
                @update:model-value="(v) => (dcrForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.refreshTokenDays', {}, 'Refresh token lifetime (days)')">
              <CoarTextInput
                :model-value="String(dcrForm.RefreshTokenLifetimeDays)"
                @update:model-value="(v) => (dcrForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 7))" />
            </CoarFormField>
          </div>

          <div class="grid grid-cols-3 gap-3">
            <CoarFormField :label="t('admin.realmSettings.dcr.gcTtlDays', {}, 'Garbage-collect after unused (days)')">
              <CoarTextInput
                :model-value="String(dcrForm.GcTtlDays)"
                @update:model-value="(v) => (dcrForm.GcTtlDays = Math.max(1, parseInt(v) || 90))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitIp', {}, 'Rate limit per source IP (per hour)')">
              <CoarTextInput
                :model-value="String(dcrForm.PerIpRateLimitPerHour)"
                @update:model-value="(v) => (dcrForm.PerIpRateLimitPerHour = Math.max(1, parseInt(v) || 5))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitRealm', {}, 'Rate limit per realm (per day)')">
              <CoarTextInput
                :model-value="String(dcrForm.PerRealmRateLimitPerDay)"
                @update:model-value="(v) => (dcrForm.PerRealmRateLimitPerDay = Math.max(1, parseInt(v) || 100))" />
            </CoarFormField>
          </div>

          <CoarFormField :label="t('admin.realmSettings.dcr.reservedNames', {}, 'Reserved client names (substring match, NFKC + case-insensitive)')">
            <EditableStringList
              v-model="dcrForm.ReservedNames"
              :placeholder="t('admin.realmSettings.dcr.reservedNames.placeholder', {}, 'Cocoar')" />
            <template #footer>
              <p class="text-xs text-gray-500">
                {{ t('admin.realmSettings.dcr.reservedNames.help', {}, 'Block client_name impersonation. Anything containing one of these strings is rejected at registration. Each entry is NFKC-normalised + lower-cased before comparison.') }}
              </p>
            </template>
          </CoarFormField>
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
