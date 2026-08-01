<script setup lang="ts">
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarSelect,
  CoarButton,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import EditableStringList from '@/components/EditableStringList.vue'
import type { OAuthApiDto } from '@/models/oauth'

/** Shared form state for the OAuth-API create and edit tabs. */
export interface ApiFormState {
  Name: string
  DisplayName: string
  Description: string
  Scopes: string[]
  UserClaims: string[]
  Enabled: boolean
  /** Empty = unassigned, otherwise an App.Id. */
  AppId: string
  /** Subset of the linked App's catalog (AppPermission.Id values). */
  PermissionIds: Set<string>
  AllowDynamicRegistration: boolean
}

export type ApiFormSection = 'identity' | 'linkage' | 'surface' | 'options'

export interface AppOption { value: string; label: string }
export interface CatalogEntry { Id: string; Resource: string; Action: string; Description?: string | null }

const props = defineProps<{
  /** Reactive form object — sections bind directly (two-way) to its fields. */
  form: ApiFormState
  section: ApiFormSection
  isCreate: boolean
  appOptions: AppOption[]
  linkedAppCatalog: CatalogEntry[]
  dto: OAuthApiDto | null
  loading: boolean
}>()

const emit = defineEmits<{ (e: 'create-implicit-scope'): void }>()

const { t } = useI18n()

function togglePermissionId(id: string) {
  const next = new Set(props.form.PermissionIds)
  if (next.has(id)) next.delete(id); else next.add(id)
  props.form.PermissionIds = next
}

function permissionLabel(p: CatalogEntry) {
  const base = `${p.Resource}:${p.Action}`
  return p.Description ? `${base} — ${p.Description}` : base
}

</script>

<template>
  <!-- ── Identity ─────────────────────────────────────────────────── -->
  <div v-if="section === 'identity'" class="section-grid">
    <div class="identity-primary-grid">
      <CoarFormField
        :label="t('admin.oauthApis.audience', {}, 'Audience (aud)')"
        :required="isCreate"
        :hint="t('admin.oauthApis.audience.hint', {}, 'Identifier written to the token aud claim. Bare identifiers and absolute URIs are supported.\n\nExample: acme-api\nImmutable after creation.')">
        <!-- Create: editable. Edit: read-only identity — the aud is a
             stable token target referenced by issued tokens, scopes, clients and
             the resource server config, so it can't change after creation. -->
        <div v-if="!isCreate" class="aud-readonly">
          <code class="aud-value">{{ form.Name }}</code>
        </div>
        <CoarTextInput v-else v-model="form.Name" clearable
          :placeholder="t('admin.oauthApis.audience.placeholder', {}, 'acme-api')" />
      </CoarFormField>

      <CoarFormField class="active-field" layout="inline" label-position="after"
        :label="t('admin.oauthApis.enabled', {}, 'Active')"
        :hint="t('admin.oauthApis.enabled.hint', {}, 'Only active APIs can be targeted by newly issued tokens.')">
        <CoarCheckbox v-model="form.Enabled" />
      </CoarFormField>
    </div>

    <CoarFormField :label="t('admin.oauthApis.displayName', {}, 'Display name')"
      :hint="t('admin.oauthApis.displayName.hint', {}, 'Human-readable name in lists and titles; purely cosmetic and changeable anytime.')">
      <CoarTextInput v-model="form.DisplayName" clearable />
    </CoarFormField>

    <CoarFormField :label="t('admin.oauthApis.description', {}, 'Description')"
      :hint="t('admin.oauthApis.description.hint', {}, 'Optional note about what this API is for.')">
      <CoarTextInput v-model="form.Description" clearable :rows="2" />
    </CoarFormField>

  </div>

  <!-- ── Linkage & gating ─────────────────────────────────────────── -->
  <div v-else-if="section === 'linkage'" class="section-grid">
    <CoarFormField :label="t('admin.oauthApis.app', {}, 'Application')"
      :hint="t('admin.oauthApis.app.hint', {}, 'Links this API to an application’s permission catalog; UserInfo resolves the user’s permissions through it.')">
      <CoarSelect v-model="form.AppId" :options="appOptions" class="field-enum" />
    </CoarFormField>

    <CoarNotice v-if="!form.AppId" variant="warning">
      {{ t('admin.oauthApis.app.unassignedHint', {}, 'Without an application link, Modgud cannot resolve a permission catalog and emits no resource_access block for this audience.') }}
    </CoarNotice>

    <CoarFormField v-if="form.AppId"
      :label="t('admin.oauthApis.permissions', {}, 'Permission selection (application catalog)')"
      :hint="t('admin.oauthApis.permissionsHint', {}, 'Which catalog permissions this API gates on. UserInfo returns only the intersection of this selection and the user’s permissions.')">
      <CoarNotice v-if="linkedAppCatalog.length === 0" variant="warning">
        {{ t('admin.oauthApis.permissions.empty', {}, 'The application has no catalog permissions yet. Add entries there first, then select them here.') }}
      </CoarNotice>
      <div v-else class="permission-checklist mt-2">
        <CoarCheckbox v-for="p in linkedAppCatalog" :key="p.Id" class="permission-row"
          :model-value="form.PermissionIds.has(p.Id)" @update:model-value="() => togglePermissionId(p.Id)"
          :label="permissionLabel(p)" />
      </div>
    </CoarFormField>
  </div>

  <!-- ── OAuth surface ────────────────────────────────────────────── -->
  <div v-else-if="section === 'surface'" class="section-grid surface-grid surface-grid--fill">
    <EditableStringList
      v-model="form.Scopes"
      appearance="compact-grid"
      fill-available
      min-height="100%"
      :header-label="t('admin.oauthApis.scopes', {}, 'Scopes')"
      :header-hint="t('admin.oauthApis.scopes.hint', {}, 'Scopes a client may request to obtain tokens for this API.')"
      :placeholder="t('admin.oauthApis.scope.placeholder', {}, 'acme.read')" />
    <EditableStringList
      v-model="form.UserClaims"
      appearance="compact-grid"
      fill-available
      min-height="100%"
      :header-label="t('admin.oauthApis.userClaims', {}, 'User claims')"
      :header-hint="t('admin.oauthApis.userClaims.hint', {}, 'User claims included in access tokens for this API.')"
      :placeholder="t('admin.oauthApis.userClaim.placeholder', {}, 'email')" />
  </div>

  <!-- ── Options ──────────────────────────────────────────────────── -->
  <div v-else-if="section === 'options'" class="section-grid options-grid">
    <CoarFormField layout="inline" label-position="after"
      :label="t('admin.oauthApis.allowDcr', {}, 'Allow dynamically registered clients')"
      :hint="t('admin.oauthApis.allowDcr.help', {}, 'DCR clients may target this API only when the realm, API and requested scope all allow it.')">
      <CoarCheckbox v-model="form.AllowDynamicRegistration" />
    </CoarFormField>

    <CoarNotice v-if="!isCreate && dto && !dto.HasImplicitScope" variant="info" class="options-full">
      <div class="flex items-center gap-3">
        <div class="flex flex-col min-w-0 flex-1">
          <div class="text-sm font-medium">
            {{ t('admin.oauthApis.implicitScope.title', {}, 'No matching OAuth scope created') }}
          </div>
          <div class="text-xs text-gray-600">
            {{ t('admin.oauthApis.implicitScope.hint', {}, 'Clients need a scope to request this API. Creates a scope with the same name (Resources = audience, hidden from discovery).') }}
          </div>
        </div>
        <CoarButton size="s" icon-start="plus" :loading="loading" @click="emit('create-implicit-scope')">
          {{ t('admin.oauthApis.implicitScope.button', {}, 'Create scope') }}
        </CoarButton>
      </div>
    </CoarNotice>
  </div>

</template>

<style scoped>
.section-grid {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}
.field-hint {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-top: 4px;
}
.field-enum {
  max-width: 22rem;
}
.identity-primary-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(12rem, 0.45fr);
  gap: 1rem;
}
.active-field {
  align-self: start;
  min-width: 0;
  padding-top: 1.65rem;
}
.aud-readonly {
  display: flex;
  align-items: center;
}
.aud-value {
  display: block;
  width: 100%;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.82rem;
  padding: 0.55rem 0.65rem;
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  word-break: break-all;
}
.permission-checklist {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 16rem;
  overflow-y: auto;
  padding: 8px;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
}
.permission-row {
  font-size: 0.82rem;
  padding: 2px 4px;
  border-radius: var(--coar-radius-s, 3px);
}
.permission-row:hover {
  background: var(--coar-background-neutral-tertiary, #f3f4f6);
}
.surface-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
}
.surface-grid--fill {
  height: 100%;
  min-height: 0;
}
.options-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 0.75rem 1.5rem;
}
.options-full {
  grid-column: 1 / -1;
}
@media (max-width: 760px) {
  .identity-primary-grid,
  .surface-grid,
  .options-grid {
    grid-template-columns: 1fr;
  }

  .active-field {
    padding-top: 0;
  }
}
</style>
