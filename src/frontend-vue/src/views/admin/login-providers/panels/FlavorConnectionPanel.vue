<script setup lang="ts">
import { computed } from 'vue'
import { CoarFormField, CoarTextInput, CoarCheckbox, CoarSelect } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import type { FlavorConfigFieldDto } from '@/models/loginProvider'

const props = withDefaults(defineProps<{
  schema: FlavorConfigFieldDto[]
  modelValue: Record<string, unknown>
  /** Only render fields belonging to this section (default 'connection'). */
  section?: string
  /** Optional subset used to split one schema section into visual groups. */
  includeKeys?: string[]
  /** Field-level errors supplied by compound validation in the parent. */
  fieldErrors?: Record<string, string>
  /** Number of layout columns used by this panel. */
  columns?: 1 | 2
}>(), { section: 'connection', columns: 2 })

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, unknown>): void
}>()

// Fields whose Section matches this panel. A missing Section means 'connection'
// (backwards-compatible with OIDC flavors that predate sections).
const { t } = useI18n()

const fields = computed(() => {
  const selected = props.schema.filter((field) =>
    (field.Section ?? 'connection') === props.section
    && (!props.includeKeys || props.includeKeys.includes(field.Key))
  )

  if (!props.includeKeys) return selected
  return selected.sort(
    (left, right) => props.includeKeys!.indexOf(left.Key) - props.includeKeys!.indexOf(right.Key),
  )
})

const fieldCopy: Record<string, { label: string; help: string; placeholder?: string }> = {
  TenantId: {
    label: 'Tenant-ID',
    help: 'Entra-Tenant als Directory-ID oder verifizierte Domain; alternativ „common“, „organizations“ oder „consumers“.',
    placeholder: 'contoso.onmicrosoft.com',
  },
  MetadataUri: {
    label: 'Discovery-URL',
    help: 'OpenID-Connect-Discovery-Endpunkt mit der Well-known-Konfiguration.',
  },
  MetadataUrl: {
    label: 'IdP-Metadaten-URL',
    help: 'Öffentliche URL, unter der der IdP seine Federation-Metadaten bereitstellt.',
  },
  MetadataXml: {
    label: 'IdP-Metadaten-XML',
    help: 'Alternative zur URL, falls Modgud die Metadaten nicht direkt vom IdP abrufen kann.',
  },
  UsePkce: {
    label: 'PKCE verwenden',
    help: 'Sendet im Authorization-Code-Flow eine PKCE Code Challenge. Nur für inkompatible Legacy-IdPs deaktivieren.',
  },
  GetClaimsFromUserInfoEndpoint: {
    label: 'Claims vom UserInfo-Endpunkt laden',
    help: 'Ruft nach dem Token-Austausch den UserInfo-Endpunkt für den vollständigen Claim-Satz auf.',
  },
  SaveTokens: {
    label: 'IdP-Tokens speichern',
    help: 'Persistiert Access-, Refresh- und ID-Token im externen Auth-Ticket. Nur aktivieren, wenn nachgelagerte Prozesse den IdP aufrufen.',
  },
  Prompt: {
    label: 'Prompt',
    help: 'OIDC-prompt-Parameter für jede Anmeldung. Leer lässt den IdP entscheiden.',
  },
  WantAssertionsSigned: {
    label: 'Signierte Assertions verlangen',
    help: 'Lehnt SAML-Antworten ohne signierte Assertion ab. Diese Absicherung sollte normalerweise aktiv bleiben.',
  },
  WantResponseSigned: {
    label: 'Signierte Response verlangen',
    help: 'Verlangt zusätzlich eine Signatur auf dem äußeren Response-Element. Viele IdPs signieren standardmäßig nur die Assertion.',
  },
  SignAuthnRequest: {
    label: 'AuthnRequest signieren',
    help: 'Signiert ausgehende SAML-AuthnRequests mit dem SP-Schlüssel dieses Realms.',
  },
  WantAssertionsEncrypted: {
    label: 'Verschlüsselte Assertions verlangen',
    help: 'Verlangt XML-verschlüsselte Assertions zusätzlich zur Signatur. Nur aktivieren, wenn der IdP entsprechend konfiguriert ist.',
  },
  NameIdFormat: {
    label: 'NameID-Format',
    help: 'Angefordertes NameID-Format im AuthnRequest.',
  },
  EntityId: {
    label: 'IdP Entity-ID',
    help: 'Optionaler Override. Leer lassen, um die Entity-ID aus den IdP-Metadaten zu übernehmen.',
    placeholder: 'https://idp.example.com/saml',
  },
  MetadataRefreshIntervalSeconds: {
    label: 'Metadaten-Aktualisierung',
    help: 'Intervall für das erneute Laden der IdP-Metadaten und rotierter Signaturzertifikate.',
  },
}

function fieldLabel(field: FlavorConfigFieldDto) {
  return t(`admin.loginProviders.flavorFields.${field.Key}.label`, {}, fieldCopy[field.Key]?.label ?? field.Label)
}

function fieldHelp(field: FlavorConfigFieldDto) {
  const fallback = fieldCopy[field.Key]?.help ?? field.HelpText ?? ''
  return fallback
    ? t(`admin.loginProviders.flavorFields.${field.Key}.help`, {}, fallback)
    : undefined
}

function fieldPlaceholder(field: FlavorConfigFieldDto) {
  return t(
    `admin.loginProviders.flavorFields.${field.Key}.placeholder`,
    {},
    fieldCopy[field.Key]?.placeholder ?? field.Placeholder ?? '',
  )
}

function fieldError(field: FlavorConfigFieldDto) {
  const externalError = props.fieldErrors?.[field.Key]
  if (externalError) return externalError
  if (!field.Required) return ''

  const value = props.modelValue[field.Key]
  const missing = value === undefined
    || value === null
    || (typeof value === 'string' && value.trim() === '')

  return missing
    ? t(
        'admin.loginProviders.validation.requiredField',
        { field: fieldLabel(field) },
        `${fieldLabel(field)} ist erforderlich.`,
      )
    : ''
}

function selectOptions(field: FlavorConfigFieldDto) {
  return (field.Options ?? []).map((option) => ({
    value: option.Value,
    label: translatedOptionLabel(field.Key, option.Value, option.Label),
  }))
}

function translatedOptionLabel(fieldKey: string, value: string, fallback: string) {
  const known: Record<string, Record<string, string>> = {
    Prompt: {
      '': 'Standard (keine Vorgabe)',
      login: 'login — erneute Anmeldung erzwingen',
      select_account: 'select_account — Kontoauswahl anzeigen',
      consent: 'consent — Zustimmung erneut anfordern',
      none: 'none — ohne Benutzerinteraktion',
    },
    NameIdFormat: {
      'urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress': 'E-Mail-Adresse',
      'urn:oasis:names:tc:SAML:2.0:nameid-format:persistent': 'Persistent',
      'urn:oasis:names:tc:SAML:2.0:nameid-format:transient': 'Transient',
      'urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified': 'Nicht spezifiziert',
    },
    MetadataRefreshIntervalSeconds: {
      '3600': '1 Stunde',
      '21600': '6 Stunden',
      '86400': '24 Stunden',
      '604800': '7 Tage',
    },
  }
  return known[fieldKey]?.[value] ?? fallback
}

function update(key: string, value: unknown, current: Record<string, unknown>) {
  emit('update:modelValue', { ...current, [key]: value })
}
</script>

<template>
  <div class="flavor-fields" :class="{ 'flavor-fields--single': columns === 1 }">
    <p v-if="fields.length === 0" class="help-text">—</p>
    <template v-for="field in fields" :key="field.Key">
      <CoarFormField
        v-if="field.Type === 'Boolean'"
        class="flavor-field"
        :label="fieldLabel(field)"
        :hint="fieldHelp(field)"
        :error="fieldError(field)"
        layout="inline"
        label-position="after">
        <CoarCheckbox
          :model-value="!!modelValue[field.Key]"
          @update:model-value="(v: boolean) => update(field.Key, v, modelValue)"
        />
      </CoarFormField>
      <CoarFormField
        v-else
        class="flavor-field"
        :class="{ 'flavor-field--wide': field.Type === 'MultilineText' }"
        :label="fieldLabel(field)"
        :hint="fieldHelp(field)"
        :error="fieldError(field)"
        :required="field.Required">
        <CoarTextInput
          v-if="field.Type === 'MultilineText'"
          :model-value="(modelValue[field.Key] as string) ?? ''"
          :placeholder="fieldPlaceholder(field)"
          :rows="6"
          clearable
          @update:model-value="(v: string) => update(field.Key, v, modelValue)"
        />
        <CoarTextInput
          v-else-if="field.Type === 'String' || field.Type === 'Url'"
          :model-value="(modelValue[field.Key] as string) ?? ''"
          :placeholder="fieldPlaceholder(field)"
          clearable
          @update:model-value="(v: string) => update(field.Key, v, modelValue)"
        />
        <CoarSelect
          v-else-if="field.Type === 'Select'"
          :model-value="modelValue[field.Key] == null ? '' : String(modelValue[field.Key])"
          :options="selectOptions(field)"
          @update:model-value="(v: string | null) => update(field.Key, v ?? '', modelValue)"
        />
        <CoarTextInput
          v-else
          :model-value="(modelValue[field.Key] as string) ?? ''"
          :placeholder="fieldPlaceholder(field)"
          clearable
          @update:model-value="(v: string) => update(field.Key, v, modelValue)"
        />
      </CoarFormField>
    </template>
    <slot />
  </div>
</template>

<style scoped>
.flavor-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.875rem 1rem;
  align-items: start;
}

.flavor-fields--single {
  grid-template-columns: minmax(0, 1fr);
}

.flavor-field {
  min-width: 0;
}

.flavor-field--wide,
.help-text {
  grid-column: 1 / -1;
}

.help-text {
  font-size: 0.8rem;
  color: #6b7280;
  margin: 0;
}

@media (max-width: 900px) {
  .flavor-fields {
    grid-template-columns: 1fr;
  }
}
</style>
