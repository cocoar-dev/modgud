<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect, CoarNote,
  CoarTabGroup, CoarTab, CoarMultiSelect,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import { useApplicationsStore } from '@/stores/applications.store'
import { useGroupStore } from '@/stores/group.store'
import type { ApplicationSettingsDto } from '@/models/application'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useApplicationsStore()
const groupStore = useGroupStore()
const loading = ref(false)
const error = ref<string | null>(null)
const activeTab = ref<'origin' | 'registration' | 'grants' | 'oauth'>('origin')

const groupOptions = computed(() =>
  groupStore.groups.map((g) => ({ value: g.Id, label: g.Name })))

// Per-section "override" toggle (on → this App overrides the realm; off → inherit).
// Numbers are kept as strings (empty = inherit that field). Booleans inside an
// overridden section are concrete; the section toggle is the inherit control.
const f = reactive({
  origin: { override: false, subdomain: '' },
  branding: { override: false, productName: '', primaryColor: '' },
  emailBranding: { override: false, productName: '' },
  selfReg: {
    override: false,
    posture: '' as '' | 'Off' | 'JitOnOtp' | 'ExplicitEndpoint' | 'InviteCode',
    enabled: false,
    requireEmailVerification: true,
    requireAdminApproval: false,
    allowedEmailDomains: [] as string[],
    defaultGroupIds: [] as string[],
    termsOfServiceUrl: '',
    privacyPolicyUrl: '',
  },
  registrationFields: {
    override: false,
    username: '' as '' | 'Off' | 'Optional' | 'Required',
    firstname: '' as '' | 'Off' | 'Optional' | 'Required',
    lastname: '' as '' | 'Off' | 'Optional' | 'Required',
  },
  nativeGrants: { override: false, enabled: false, access: '', refresh: '' },
  dcr: {
    override: false, enabled: false, access: '', refresh: '',
    reservedNames: [] as string[], perIp: '', perRealm: '',
  },
  cimd: { override: false, enabled: false, access: '', refresh: '' },
})

const postureOptions = [
  { value: '', label: t('admin.appSettings.inherit', {}, '(vom Realm erben)') },
  { value: 'Off', label: t('admin.appSettings.posture.off', {}, 'Off — keine Selbstregistrierung') },
  { value: 'JitOnOtp', label: t('admin.appSettings.posture.jit', {}, 'JIT-on-OTP (passwortlos)') },
  { value: 'ExplicitEndpoint', label: t('admin.appSettings.posture.explicit', {}, 'Expliziter Endpoint') },
  { value: 'InviteCode', label: t('admin.appSettings.posture.inviteCode', {}, 'Invite code (invite-only)') },
]

// Tri-state per identity field; '' = inherit the realm requirement for that field.
const requirementOptions = [
  { value: '', label: t('admin.appSettings.inherit', {}, '(vom Realm erben)') },
  { value: 'Off', label: t('admin.regFields.off', {}, 'Aus') },
  { value: 'Optional', label: t('admin.regFields.optional', {}, 'Optional') },
  { value: 'Required', label: t('admin.regFields.required', {}, 'Pflicht') },
]

function numStr(n?: number | null): string {
  return n === null || n === undefined ? '' : String(n)
}
function parseNum(s: string): number | null {
  const v = s.trim()
  if (v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null
}

onMounted(async () => {
  loading.value = true
  try {
    // Load groups too so the DefaultGroups picker can label by name.
    const [s] = await Promise.all([store.loadSettings(props.id), groupStore.initialize()])
    if (s.Origin) { f.origin.override = true; f.origin.subdomain = s.Origin.Subdomain ?? '' }
    if (s.Branding) {
      f.branding.override = true
      f.branding.productName = s.Branding.ProductName ?? ''
      f.branding.primaryColor = s.Branding.PrimaryColor ?? ''
    }
    if (s.EmailBranding) {
      f.emailBranding.override = true
      f.emailBranding.productName = s.EmailBranding.ProductName ?? ''
    }
    if (s.SelfRegistration) {
      const sr = s.SelfRegistration
      f.selfReg.override = true
      f.selfReg.posture = (sr.Posture as any) ?? ''
      f.selfReg.enabled = sr.Enabled ?? false
      f.selfReg.requireEmailVerification = sr.RequireEmailVerification ?? true
      f.selfReg.requireAdminApproval = sr.RequireAdminApproval ?? false
      f.selfReg.allowedEmailDomains = sr.AllowedEmailDomains ?? []
      f.selfReg.defaultGroupIds = sr.DefaultGroupIds ?? []
      f.selfReg.termsOfServiceUrl = sr.TermsOfServiceUrl ?? ''
      f.selfReg.privacyPolicyUrl = sr.PrivacyPolicyUrl ?? ''
    }
    if (s.RegistrationFields) {
      const rf = s.RegistrationFields
      f.registrationFields.override = true
      f.registrationFields.username = (rf.Username as any) ?? ''
      f.registrationFields.firstname = (rf.Firstname as any) ?? ''
      f.registrationFields.lastname = (rf.Lastname as any) ?? ''
    }
    if (s.NativeGrants) {
      f.nativeGrants.override = true
      f.nativeGrants.enabled = s.NativeGrants.Enabled ?? false
      f.nativeGrants.access = numStr(s.NativeGrants.AccessTokenLifetimeMinutes)
      f.nativeGrants.refresh = numStr(s.NativeGrants.RefreshTokenLifetimeDays)
    }
    if (s.Dcr) {
      f.dcr.override = true
      f.dcr.enabled = s.Dcr.Enabled ?? false
      f.dcr.access = numStr(s.Dcr.AccessTokenLifetimeMinutes)
      f.dcr.refresh = numStr(s.Dcr.RefreshTokenLifetimeDays)
      f.dcr.reservedNames = s.Dcr.ReservedNames ?? []
      f.dcr.perIp = numStr(s.Dcr.PerIpRateLimitPerHour)
      f.dcr.perRealm = numStr(s.Dcr.PerRealmRateLimitPerDay)
    }
    if (s.Cimd) {
      f.cimd.override = true
      f.cimd.enabled = s.Cimd.Enabled ?? false
      f.cimd.access = numStr(s.Cimd.AccessTokenLifetimeMinutes)
      f.cimd.refresh = numStr(s.Cimd.RefreshTokenLifetimeDays)
    }
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
})

function buildDto(): ApplicationSettingsDto {
  // An overridden section sends its values; a non-overridden section sends {}
  // (empty = clear → inherit the realm), so the saved doc matches the toggles.
  return {
    Origin: f.origin.override ? { Subdomain: f.origin.subdomain.trim() || null } : {},
    Branding: f.branding.override
      ? { ProductName: f.branding.productName.trim() || null, PrimaryColor: f.branding.primaryColor.trim() || null }
      : {},
    EmailBranding: f.emailBranding.override
      ? { ProductName: f.emailBranding.productName.trim() || null }
      : {},
    SelfRegistration: f.selfReg.override
      ? {
          Posture: f.selfReg.posture || null,
          Enabled: f.selfReg.enabled,
          RequireEmailVerification: f.selfReg.requireEmailVerification,
          RequireAdminApproval: f.selfReg.requireAdminApproval,
          AllowedEmailDomains: f.selfReg.allowedEmailDomains.length ? f.selfReg.allowedEmailDomains : null,
          DefaultGroupIds: f.selfReg.defaultGroupIds.length ? f.selfReg.defaultGroupIds : null,
          TermsOfServiceUrl: f.selfReg.termsOfServiceUrl.trim() || null,
          PrivacyPolicyUrl: f.selfReg.privacyPolicyUrl.trim() || null,
        }
      : {},
    RegistrationFields: f.registrationFields.override
      ? {
          Username: f.registrationFields.username || null,
          Firstname: f.registrationFields.firstname || null,
          Lastname: f.registrationFields.lastname || null,
        }
      : {},
    NativeGrants: f.nativeGrants.override
      ? { Enabled: f.nativeGrants.enabled, AccessTokenLifetimeMinutes: parseNum(f.nativeGrants.access), RefreshTokenLifetimeDays: parseNum(f.nativeGrants.refresh) }
      : {},
    Dcr: f.dcr.override
      ? {
          Enabled: f.dcr.enabled,
          AccessTokenLifetimeMinutes: parseNum(f.dcr.access),
          RefreshTokenLifetimeDays: parseNum(f.dcr.refresh),
          ReservedNames: f.dcr.reservedNames.length ? f.dcr.reservedNames : null,
          PerIpRateLimitPerHour: parseNum(f.dcr.perIp),
          PerRealmRateLimitPerDay: parseNum(f.dcr.perRealm),
        }
      : {},
    Cimd: f.cimd.override
      ? { Enabled: f.cimd.enabled, AccessTokenLifetimeMinutes: parseNum(f.cimd.access), RefreshTokenLifetimeDays: parseNum(f.cimd.refresh) }
      : {},
  }
}

async function save() {
  loading.value = true
  error.value = null
  try {
    await store.saveSettings(props.id, buildDto())
    props.close(true)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

const footerButton = computed(() => ({
  visible: true,
  text: t('common.save', {}, 'Speichern'),
  disabled: loading.value,
  loading: loading.value,
  onClick: save,
}))
</script>

<template>
  <ModalLayout :close="close" :title="t('admin.appSettings.title', {}, 'Application-Einstellungen')"
    icon="sliders-horizontal" :footer-button="footerButton" width="46rem">
    <div class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote variant="info">
        {{ t('admin.appSettings.hint', {}, 'Diese Einstellungen überschreiben die Realm-Defaults nur für diese App. Ein deaktivierter Abschnitt erbt vom Realm.') }}
      </CoarNote>

      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="origin">{{ t('admin.appSettings.tabs.origin', {}, 'Origin & Branding') }}</CoarTab>
        <CoarTab id="registration">{{ t('admin.appSettings.tabs.registration', {}, 'Registrierung') }}</CoarTab>
        <CoarTab id="grants">{{ t('admin.appSettings.tabs.grants', {}, 'Native Grants') }}</CoarTab>
        <CoarTab id="oauth">{{ t('admin.appSettings.tabs.oauth', {}, 'OAuth (DCR/CIMD)') }}</CoarTab>
      </CoarTabGroup>

      <!-- Origin & Branding -->
      <div v-show="activeTab === 'origin'" class="tab-content">
        <CoarCheckbox v-model="f.origin.override" :label="t('admin.appSettings.origin.override', {}, 'Eigene Subdomain für diese App')" />
        <CoarFormField v-if="f.origin.override" :label="t('admin.appSettings.origin.subdomain', {}, 'Subdomain (Child der Realm-Primary-Domain)')">
          <CoarTextInput v-model="f.origin.subdomain" clearable placeholder="amzettel.cocoar.app" />
        </CoarFormField>

        <CoarCheckbox v-model="f.branding.override" :label="t('admin.appSettings.branding.override', {}, 'Eigenes Branding (Login/SPA)')" />
        <template v-if="f.branding.override">
          <CoarFormField :label="t('admin.appSettings.branding.productName', {}, 'Produktname')">
            <CoarTextInput v-model="f.branding.productName" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.branding.primaryColor', {}, 'Primärfarbe (CSS)')">
            <CoarTextInput v-model="f.branding.primaryColor" clearable placeholder="#1077be" />
          </CoarFormField>
        </template>

        <CoarCheckbox v-model="f.emailBranding.override" :label="t('admin.appSettings.email.override', {}, 'Eigenes E-Mail-Branding')" />
        <CoarFormField v-if="f.emailBranding.override" :label="t('admin.appSettings.email.productName', {}, 'Produktname in E-Mails')">
          <CoarTextInput v-model="f.emailBranding.productName" clearable />
        </CoarFormField>
      </div>

      <!-- Registration -->
      <div v-show="activeTab === 'registration'" class="tab-content">
        <CoarCheckbox v-model="f.selfReg.override" :label="t('admin.appSettings.selfReg.override', {}, 'Eigene Registrierungs-Policy')" />
        <template v-if="f.selfReg.override">
          <CoarFormField :label="t('admin.appSettings.selfReg.posture', {}, 'Posture (passwortlose Registrierung)')">
            <CoarSelect v-model="f.selfReg.posture" :options="postureOptions" />
          </CoarFormField>

          <!-- ADR-0012 — how to actually use the InviteCode posture. The admin-UI
               path works immediately; M2M minting needs a one-time scope + SA setup. -->
          <div v-if="f.selfReg.posture === 'InviteCode'"
            class="rounded border border-amber-300 bg-amber-50 dark:border-amber-700/50 dark:bg-amber-900/20 p-3 text-sm space-y-1">
            <p class="font-medium">{{ t('admin.appSettings.posture.inviteCode.title', {}, 'Invite-code posture: how to hand out codes') }}</p>
            <p>{{ t('admin.appSettings.posture.inviteCode.ui', {}, 'Works immediately: mint codes in the admin under “Invite Codes” (sidebar, OAuth & Federation) for this app — no further setup required.') }}</p>
            <p>{{ t('admin.appSettings.posture.inviteCode.m2m', {}, 'For automatic minting by the backend app (M2M): create an OAuth scope “invite:write” bound to this app (App-ID set), and give a ServiceAccount a credential carrying that scope. The app then calls POST /api/app/{appId}/invite-codes with its client_credentials token.') }}</p>
            <p class="text-gray-500">{{ t('admin.appSettings.posture.inviteCode.redeem', {}, 'Redemption: the code travels on the native sign-up request (InviteCode field); unknown emails become users only with a valid, unused code. Existing confirmed users sign in normally (the code is ignored).') }}</p>
          </div>
          <CoarCheckbox v-model="f.selfReg.enabled" :label="t('admin.appSettings.selfReg.enabled', {}, 'Self-Registration aktiv')" />
          <CoarCheckbox v-model="f.selfReg.requireEmailVerification" :label="t('admin.appSettings.selfReg.verify', {}, 'E-Mail-Verifizierung erforderlich')" />
          <CoarCheckbox v-model="f.selfReg.requireAdminApproval" :label="t('admin.appSettings.selfReg.approval', {}, 'Admin-Freigabe erforderlich')" />
          <CoarFormField :label="t('admin.appSettings.selfReg.domains', {}, 'Erlaubte E-Mail-Domains (leer = alle)')">
            <EditableStringList v-model="f.selfReg.allowedEmailDomains" />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.selfReg.defaultGroups', {}, 'Standard-Gruppen (Auto-Mitgliedschaft nach Verifizierung)')">
            <CoarMultiSelect
              v-model="f.selfReg.defaultGroupIds"
              :options="groupOptions"
              searchable
              clearable
              :placeholder="t('admin.appSettings.selfReg.defaultGroups.placeholder', {}, 'Gruppen wählen…')" />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.selfReg.tos', {}, 'AGB-URL')">
            <CoarTextInput v-model="f.selfReg.termsOfServiceUrl" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.selfReg.privacy', {}, 'Datenschutz-URL')">
            <CoarTextInput v-model="f.selfReg.privacyPolicyUrl" clearable />
          </CoarFormField>
        </template>

        <CoarCheckbox v-model="f.registrationFields.override" :label="t('admin.appSettings.regFields.override', {}, 'Eigene Pflichtfelder bei Registrierung')" />
        <template v-if="f.registrationFields.override">
          <CoarNote variant="info">
            {{ t('admin.appSettings.regFields.hint', {}, 'Welche Identitätsfelder bei der Kontoerstellung gefordert sind. E-Mail ist immer Pflicht. Native Clients müssen geforderte Felder erfassen.') }}
          </CoarNote>
          <CoarFormField :label="t('admin.regFields.username', {}, 'Benutzername')">
            <CoarSelect v-model="f.registrationFields.username" :options="requirementOptions" />
          </CoarFormField>
          <CoarFormField :label="t('admin.regFields.firstname', {}, 'Vorname')">
            <CoarSelect v-model="f.registrationFields.firstname" :options="requirementOptions" />
          </CoarFormField>
          <CoarFormField :label="t('admin.regFields.lastname', {}, 'Nachname')">
            <CoarSelect v-model="f.registrationFields.lastname" :options="requirementOptions" />
          </CoarFormField>
        </template>
      </div>

      <!-- Native Grants -->
      <div v-show="activeTab === 'grants'" class="tab-content">
        <CoarCheckbox v-model="f.nativeGrants.override" :label="t('admin.appSettings.grants.override', {}, 'Eigene Native-Grant-Settings')" />
        <template v-if="f.nativeGrants.override">
          <CoarCheckbox v-model="f.nativeGrants.enabled" :label="t('admin.appSettings.grants.enabled', {}, 'Native Grants aktiv')" />
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
              <CoarTextInput v-model="f.nativeGrants.access" clearable placeholder="15" />
            </CoarFormField>
            <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
              <CoarTextInput v-model="f.nativeGrants.refresh" clearable placeholder="14" />
            </CoarFormField>
          </div>
        </template>
      </div>

      <!-- OAuth (DCR / CIMD) -->
      <div v-show="activeTab === 'oauth'" class="tab-content">
        <CoarCheckbox v-model="f.dcr.override" :label="t('admin.appSettings.dcr.override', {}, 'Eigene DCR-Settings')" />
        <template v-if="f.dcr.override">
          <CoarCheckbox v-model="f.dcr.enabled" :label="t('admin.appSettings.dcr.enabled', {}, 'Dynamic Client Registration aktiv')" />
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
              <CoarTextInput v-model="f.dcr.access" clearable placeholder="15" />
            </CoarFormField>
            <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
              <CoarTextInput v-model="f.dcr.refresh" clearable placeholder="7" />
            </CoarFormField>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.appSettings.dcr.perIp', {}, 'Rate-Limit pro IP / Stunde')">
              <CoarTextInput v-model="f.dcr.perIp" clearable placeholder="5" />
            </CoarFormField>
            <CoarFormField :label="t('admin.appSettings.dcr.perRealm', {}, 'Rate-Limit pro Realm / Tag')">
              <CoarTextInput v-model="f.dcr.perRealm" clearable placeholder="100" />
            </CoarFormField>
          </div>
          <CoarFormField :label="t('admin.appSettings.dcr.reservedNames', {}, 'Reservierte Client-Namen (Blockliste)')">
            <EditableStringList v-model="f.dcr.reservedNames" />
          </CoarFormField>
        </template>

        <CoarCheckbox v-model="f.cimd.override" :label="t('admin.appSettings.cimd.override', {}, 'Eigene CIMD-Settings')" />
        <template v-if="f.cimd.override">
          <CoarCheckbox v-model="f.cimd.enabled" :label="t('admin.appSettings.cimd.enabled', {}, 'CIMD aktiv')" />
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
              <CoarTextInput v-model="f.cimd.access" clearable placeholder="15" />
            </CoarFormField>
            <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
              <CoarTextInput v-model="f.cimd.refresh" clearable placeholder="7" />
            </CoarFormField>
          </div>
        </template>
      </div>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.tab-bar { margin-bottom: 8px; }
.tab-content { display: flex; flex-direction: column; gap: 12px; min-height: 0; }
</style>
