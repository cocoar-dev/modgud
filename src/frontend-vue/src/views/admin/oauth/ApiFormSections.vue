<script setup lang="ts">
import { computed } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarSelect,
  CoarButton,
  CoarTag,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import AppNote from '@/components/AppNote.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import type { OAuthApiDto } from '@/models/oauth'

/** Shared form state for the OAuth-API modal (create wizard + edit tabs). */
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

export type ApiFormSection = 'identity' | 'linkage' | 'surface' | 'options' | 'review'

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

const appLabel = computed(() => {
  if (!props.form.AppId) return t('admin.oauthApis.app.unassigned', {}, '— Unassigned (no UserInfo emission)')
  return props.appOptions.find((o) => o.value === props.form.AppId)?.label ?? props.form.AppId
})

function togglePermissionId(id: string) {
  const next = new Set(props.form.PermissionIds)
  if (next.has(id)) next.delete(id); else next.add(id)
  props.form.PermissionIds = next
}

function permissionLabel(p: CatalogEntry) {
  const base = `${p.Resource}:${p.Action}`
  return p.Description ? `${base} — ${p.Description}` : base
}

async function copyAudience() {
  try { await navigator.clipboard.writeText(props.form.Name) } catch { /* ignore */ }
}
</script>

<template>
  <!-- ── Identity ─────────────────────────────────────────────────── -->
  <div v-if="section === 'identity'" class="section-grid">
    <CoarFormField
      :label="t('admin.oauthApis.audience', {}, 'Audience (aud)')"
      :required="isCreate">
      <!-- Create: editable. Edit: read-only identity with copy — the aud is a
           stable token target referenced by issued tokens, scopes, clients and
           the resource server config, so it can't change after creation. -->
      <div v-if="!isCreate" class="aud-readonly">
        <code class="aud-value">{{ form.Name }}</code>
        <CoarButton size="s" variant="secondary" icon-start="copy" @click="copyAudience">
          {{ t('common.copy', {}, 'Copy') }}
        </CoarButton>
        <CoarTag size="s" variant="neutral" class="aud-lock">
          {{ t('admin.oauthApis.audience.immutable', {}, 'immutable') }}
        </CoarTag>
      </div>
      <CoarTextInput v-else v-model="form.Name" clearable
        :placeholder="t('admin.oauthApis.audience.placeholder', {}, 'https://event-tree.api')" />
      <p class="field-hint">
        {{ t('admin.oauthApis.audience.hint', {}, 'The aud value of this protected resource — this exact value lands in the token (aud) and is the resource= value a client requests. Immutable after creation (tokens, scopes and clients all reference it).') }}
      </p>
    </CoarFormField>

    <CoarFormField :label="t('admin.oauthApis.displayName', {}, 'Display name')">
      <CoarTextInput v-model="form.DisplayName" clearable />
      <p class="field-hint">{{ t('admin.oauthApis.displayName.hint', {}, 'Human-readable name in lists and titles; purely cosmetic and changeable anytime.') }}</p>
    </CoarFormField>

    <CoarFormField :label="t('admin.oauthApis.description', {}, 'Description')">
      <CoarTextInput v-model="form.Description" clearable :rows="2" />
      <p class="field-hint">{{ t('admin.oauthApis.description.hint', {}, 'Optional note about what this API is for.') }}</p>
    </CoarFormField>
  </div>

  <!-- ── Linkage & gating ─────────────────────────────────────────── -->
  <div v-else-if="section === 'linkage'" class="section-grid">
    <CoarFormField :label="t('admin.oauthApis.app', {}, 'Application')">
      <CoarSelect v-model="form.AppId" :options="appOptions" class="field-enum" />
      <p class="field-hint">{{ t('admin.oauthApis.app.hint', {}, 'Links this API to an application’s permission catalog; UserInfo resolves the user’s permissions through it.') }}</p>
    </CoarFormField>

    <CoarFormField v-if="form.AppId"
      :label="t('admin.oauthApis.permissions', {}, 'Permission selection (application catalog)')">
      <p class="field-hint">{{ t('admin.oauthApis.permissionsHint', {}, 'Which catalog permissions this API gates on. UserInfo returns only the intersection of this selection and the user’s permissions.') }}</p>
      <div v-if="linkedAppCatalog.length === 0" class="text-xs text-gray-400 italic mt-2">
        {{ t('admin.oauthApis.permissions.empty', {}, 'The application has no catalog permissions yet. Add entries there first, then select them here.') }}
      </div>
      <div v-else class="permission-checklist mt-2">
        <CoarCheckbox v-for="p in linkedAppCatalog" :key="p.Id" class="permission-row"
          :model-value="form.PermissionIds.has(p.Id)" @update:model-value="() => togglePermissionId(p.Id)"
          :label="permissionLabel(p)" />
      </div>
    </CoarFormField>
  </div>

  <!-- ── OAuth surface ────────────────────────────────────────────── -->
  <div v-else-if="section === 'surface'" class="section-grid">
    <CoarFormField :label="t('admin.oauthApis.scopes', {}, 'Scopes')">
      <EditableStringList v-model="form.Scopes"
        :placeholder="t('admin.oauthApis.scope.placeholder', {}, 'event-tree.api')" />
      <p class="field-hint">{{ t('admin.oauthApis.scopes.hint', {}, 'Scopes a client may request to obtain tokens for this API.') }}</p>
    </CoarFormField>
    <CoarFormField :label="t('admin.oauthApis.userClaims', {}, 'User claims')">
      <EditableStringList v-model="form.UserClaims"
        :placeholder="t('admin.oauthApis.userClaim.placeholder', {}, 'email')" />
      <p class="field-hint">{{ t('admin.oauthApis.userClaims.hint', {}, 'User claims included in access tokens for this API.') }}</p>
    </CoarFormField>
  </div>

  <!-- ── Options ──────────────────────────────────────────────────── -->
  <div v-else-if="section === 'options'" class="section-grid">
    <CoarFormField>
      <CoarCheckbox v-model="form.Enabled" :label="t('common.enabled', {}, 'Enabled')" />
      <p class="field-hint">{{ t('admin.oauthApis.enabled.hint', {}, 'Disabled APIs no longer accept tokens.') }}</p>
    </CoarFormField>
    <CoarFormField :label="t('admin.oauthApis.allowDcr.label', {}, 'Dynamic Client Registration (DCR)')">
      <CoarCheckbox v-model="form.AllowDynamicRegistration"
        :label="t('admin.oauthApis.allowDcr', {}, 'DCR clients may request this API')" />
      <p class="field-hint">{{ t('admin.oauthApis.allowDcr.help', {}, 'Off by default: dynamically registered clients cannot request tokens for this API until allowed here.') }}</p>
    </CoarFormField>

    <AppNote v-if="!isCreate && dto && !dto.HasImplicitScope" variant="info" :truncate="false">
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
    </AppNote>
  </div>

  <!-- ── Review (create wizard only) ──────────────────────────────── -->
  <div v-else-if="section === 'review'" class="section-grid">
    <p class="field-hint">{{ t('admin.oauthApis.review.hint', {}, 'Review the details and create the API. The audience is immutable afterwards.') }}</p>
    <dl class="review-list">
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.audience', {}, 'Audience (aud)') }}</dt>
        <dd><code class="aud-value">{{ form.Name || '—' }}</code></dd>
      </div>
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.displayName', {}, 'Display name') }}</dt>
        <dd>{{ form.DisplayName || '—' }}</dd>
      </div>
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.app', {}, 'Application') }}</dt>
        <dd>{{ appLabel }}</dd>
      </div>
      <div v-if="form.AppId" class="review-row">
        <dt>{{ t('admin.oauthApis.review.permissions', {}, 'Permissions') }}</dt>
        <dd>{{ form.PermissionIds.size }}</dd>
      </div>
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.scopes', {}, 'Scopes') }}</dt>
        <dd>{{ form.Scopes.length ? form.Scopes.join(', ') : '—' }}</dd>
      </div>
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.userClaims', {}, 'User claims') }}</dt>
        <dd>{{ form.UserClaims.length ? form.UserClaims.join(', ') : '—' }}</dd>
      </div>
      <div class="review-row">
        <dt>{{ t('common.enabled', {}, 'Enabled') }}</dt>
        <dd>{{ form.Enabled ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}</dd>
      </div>
      <div class="review-row">
        <dt>{{ t('admin.oauthApis.allowDcr.label', {}, 'Dynamic Client Registration (DCR)') }}</dt>
        <dd>{{ form.AllowDynamicRegistration ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}</dd>
      </div>
    </dl>
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
.aud-readonly {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.aud-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.85rem;
  font-weight: 600;
  padding: 4px 8px;
  border-radius: var(--coar-radius-s, 3px);
  background: var(--coar-background-neutral-tertiary, #f3f4f6);
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  word-break: break-all;
}
.aud-lock {
  flex-shrink: 0;
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
.review-list {
  display: flex;
  flex-direction: column;
  gap: 0;
  margin: 0;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
  overflow: hidden;
}
.review-row {
  display: grid;
  grid-template-columns: 14rem 1fr;
  gap: 1rem;
  padding: 8px 12px;
}
.review-row:nth-child(even) {
  background: var(--coar-background-neutral-secondary, #f9fafb);
}
.review-row dt {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.review-row dd {
  margin: 0;
  font-size: 0.85rem;
  word-break: break-word;
}
</style>
