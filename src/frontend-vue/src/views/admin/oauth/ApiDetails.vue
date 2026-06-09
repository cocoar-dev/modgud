<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarSelect,
  CoarButton,
  CoarNote,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import { useOAuthApiStore } from '@/stores/oauthApi.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { OAuthApiDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthApiStore()
const applicationsStore = useApplicationsStore()
const isCreate = computed(() => props.id === 'create')

// Empty value = "unassigned" — RS exists but the IdP can't resolve a
// catalog for it, so UserInfo will not emit a resource_access block.
const appOptions = computed(() => [
  { value: '', label: t('admin.oauthApis.app.unassigned', {}, '— Unassigned (no UserInfo emission)') },
  ...applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
])
const loading = ref(false)
const error = ref<string | null>(null)

interface FormState {
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

function emptyForm(): FormState {
  return {
    Name: '',
    DisplayName: '',
    Description: '',
    Scopes: [],
    UserClaims: [],
    Enabled: true,
    AppId: '',
    PermissionIds: new Set<string>(),
    AllowDynamicRegistration: false,
  }
}

const form = ref<FormState>(emptyForm())
const dto = ref<OAuthApiDto | null>(null)

function fromDto(dto: OAuthApiDto): FormState {
  return {
    Name: dto.Name,
    DisplayName: dto.DisplayName ?? '',
    Description: dto.Description ?? '',
    Scopes: [...(dto.Scopes ?? [])],
    UserClaims: [...(dto.UserClaims ?? [])],
    Enabled: dto.Enabled,
    AppId: dto.AppId ?? '',
    PermissionIds: new Set(dto.PermissionIds ?? []),
    AllowDynamicRegistration: dto.AllowDynamicRegistration,
  }
}

/**
 * Catalog of the currently selected App, ordered for display. Empty when
 * no App is linked or the App can't be found in the loaded list.
 */
const linkedAppCatalog = computed(() => {
  if (!form.value.AppId) return []
  const app = applicationsStore.apps.find((a) => a.Id === form.value.AppId)
  if (!app) return []
  return [...(app.Permissions ?? [])].sort((a, b) => {
    const lhs = `${a.Resource}:${a.Action}`
    const rhs = `${b.Resource}:${b.Action}`
    return lhs.localeCompare(rhs)
  })
})

function togglePermissionId(id: string) {
  // Mutate via fresh Set so Vue reactivity picks up the change.
  const next = new Set(form.value.PermissionIds)
  if (next.has(id)) next.delete(id); else next.add(id)
  form.value.PermissionIds = next
}

// CoarCheckbox takes a plain-string label, so flatten the catalog entry to
// "resource:action — description" (description optional).
function permissionLabel(p: { Resource: string; Action: string; Description?: string | null }) {
  const base = `${p.Resource}:${p.Action}`
  return p.Description ? `${base} — ${p.Description}` : base
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.oauthApis.createTitle', {}, 'API erstellen')
    : (form.value.DisplayName || form.value.Name)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: !form.value.Name.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  applicationsStore.initialize()
  if (isCreate.value) return
  loading.value = true
  try {
    const loaded = await store.loadOne(props.id)
    if (!loaded) {
      error.value = t('admin.oauthApis.loadFailed', {}, 'API konnte nicht geladen werden.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.Name.trim()) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      const created = await store.create({
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Scopes: [...form.value.Scopes],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        AppId: form.value.AppId || null,
        PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
        AllowDynamicRegistration: form.value.AllowDynamicRegistration,
      })
      // Load the freshly-minted RS so subsequent edits operate on real state.
      const loaded = await store.loadOne(created.Id)
      if (loaded) {
        dto.value = loaded
        form.value = fromDto(loaded)
      }
      props.close()
    } else {
      const updated = await store.update(props.id, {
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Scopes: [...form.value.Scopes],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        // Always send — empty string detaches, guid assigns.
        AppId: form.value.AppId,
        // Detaching the App must clear the subset; the backend rejects
        // non-empty PermissionIds without an AppId.
        PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
        AllowDynamicRegistration: form.value.AllowDynamicRegistration,
      })
      dto.value = updated
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

/**
 * One-click convenience: ask the backend to mint the 1:1 OAuthScope
 * companion for this API. Hidden once `dto.HasImplicitScope` flips
 * — the call returns 409 if a scope with the same name already
 * exists, so the button is only safe to show when the flag is false.
 */
async function createImplicitScope() {
  if (isCreate.value || !dto.value) return
  loading.value = true
  error.value = null
  try {
    await store.createImplicitScope(dto.value.Id)
    const reloaded = await store.loadOne(dto.value.Id)
    if (reloaded) {
      dto.value = reloaded
      form.value = fromDto(reloaded)
    }
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="server"
    :footer-button="footerButton" width="44rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1">
      <!-- General -->
      <div class="tab-content">
        <div class="modal-form">
          <!-- Section: Identity -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.oauthApis.section.identity', {}, 'Identität') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.oauthApis.name', {}, 'Name')" required>
                <CoarTextInput v-model="form.Name" :disabled="!isCreate" clearable />
                <p class="field-hint">{{ t('admin.oauthApis.name.hint', {}, 'Audience (aud) der geschützten Ressource; das Token eines Clients muss auf diesen Namen zielen. Nach dem Anlegen unveränderlich.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.oauthApis.displayName', {}, 'Display Name')">
                <CoarTextInput v-model="form.DisplayName" clearable />
                <p class="field-hint">{{ t('admin.oauthApis.displayName.hint', {}, 'Lesbarer Anzeigename in Listen und Titeln; rein kosmetisch.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('admin.oauthApis.description', {}, 'Beschreibung')">
                <CoarTextInput v-model="form.Description" clearable :rows="2" />
                <p class="field-hint">{{ t('admin.oauthApis.description.hint', {}, 'Optionale Notiz zum Zweck dieser API.') }}</p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Linkage & gating -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.oauthApis.section.linkage', {}, 'Verknüpfung & Gating') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.oauthApis.app', {}, 'Application')">
                <CoarSelect v-model="form.AppId" :options="appOptions" />
                <p class="field-hint">{{ t('admin.oauthApis.app.hint', {}, 'Verknüpft diese API mit dem Berechtigungs-Katalog einer Anwendung; UserInfo löst die Berechtigungen des Nutzers darüber auf.') }}</p>
              </CoarFormField>

              <CoarFormField v-if="form.AppId" class="col-full"
                :label="t('admin.oauthApis.permissions', {}, 'Berechtigungs-Auswahl (Katalog der Anwendung)')">
                <p class="field-hint">{{ t('admin.oauthApis.permissionsHint', {}, 'Welche Berechtigungen des Katalogs diese API absichert. UserInfo gibt nur die Schnittmenge aus dieser Auswahl und den Berechtigungen des Nutzers zurück.') }}</p>
                <div v-if="linkedAppCatalog.length === 0" class="text-xs text-gray-400 italic mt-2">
                  {{ t('admin.oauthApis.permissions.empty', {}, 'Die Anwendung hat noch keine Berechtigungen im Katalog. Erst dort Einträge anlegen, dann hier auswählen.') }}
                </div>
                <div v-else class="permission-checklist mt-2">
                  <CoarCheckbox v-for="p in linkedAppCatalog" :key="p.Id" class="permission-row"
                    :model-value="form.PermissionIds.has(p.Id)" @update:model-value="() => togglePermissionId(p.Id)"
                    :label="permissionLabel(p)" />
                </div>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: OAuth surface -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.oauthApis.section.surface', {}, 'OAuth-Oberfläche') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full" :label="t('admin.oauthApis.scopes', {}, 'Scopes')">
                <EditableStringList
                  v-model="form.Scopes"
                  :placeholder="t('admin.oauthApis.scope.placeholder', {}, 'event-tree.api')" />
                <p class="field-hint">{{ t('admin.oauthApis.scopes.hint', {}, 'Scopes, die ein Client anfragen darf, um Tokens für diese API zu erhalten.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('admin.oauthApis.userClaims', {}, 'User-Claims')">
                <EditableStringList
                  v-model="form.UserClaims"
                  :placeholder="t('admin.oauthApis.userClaim.placeholder', {}, 'email')" />
                <p class="field-hint">{{ t('admin.oauthApis.userClaims.hint', {}, 'Nutzer-Claims, die in Access-Tokens dieser API aufgenommen werden.') }}</p>
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Options — Enabled flag and the DCR target gate. -->
          <section class="form-section">
            <h3 class="form-section-heading">{{ t('admin.oauthApis.section.options', {}, 'Optionen') }}</h3>
            <div class="modal-form-grid">
              <CoarFormField class="col-full">
                <CoarCheckbox v-model="form.Enabled" :label="t('common.enabled', {}, 'Aktiviert')" />
                <p class="field-hint">{{ t('admin.oauthApis.enabled.hint', {}, 'Deaktivierte APIs nehmen keine Tokens mehr an.') }}</p>
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('admin.oauthApis.allowDcr.label', {}, 'Dynamische Client-Registrierung (DCR)')">
                <CoarCheckbox
                  v-model="form.AllowDynamicRegistration"
                  :label="t('admin.oauthApis.allowDcr', {}, 'DCR-Clients dürfen diese API anfragen')" />
                <p class="field-hint">{{ t('admin.oauthApis.allowDcr.help', {}, 'Standardmäßig aus: dynamisch registrierte Clients können keine Tokens für diese API anfragen, solange dies nicht hier erlaubt wird.') }}</p>
              </CoarFormField>

              <!--
                Implicit-scope affordance: most APIs end up with a 1:1 OAuthScope
                companion of the same name. Docked at the END of a stable section
                so it never pops in at the top and shoves fields. Hidden when the
                scope already exists; hidden in Create mode because the API hasn't
                been minted yet (no Id to attach to).
              -->
              <CoarNote v-if="!isCreate && dto && !dto.HasImplicitScope" variant="info" class="col-full">
                <div class="flex items-center gap-3">
                  <div class="flex flex-col min-w-0 flex-1">
                    <div class="text-sm font-medium">
                      {{ t('admin.oauthApis.implicitScope.title', {}, 'Kein passender OAuth-Scope angelegt') }}
                    </div>
                    <div class="text-xs text-gray-600">
                      {{ t('admin.oauthApis.implicitScope.hint', {}, 'Clients brauchen einen Scope um diese API anzufragen. Erstellt einen Scope mit gleichem Namen (Resources = Audience, nicht im Discovery sichtbar).') }}
                    </div>
                  </div>
                  <CoarButton size="s" icon-start="plus" :loading="loading" @click="createImplicitScope">
                    {{ t('admin.oauthApis.implicitScope.button', {}, 'Scope anlegen') }}
                  </CoarButton>
                </div>
              </CoarNote>
            </div>
          </section>
        </div>
      </div>

      <p v-if="error" class="mt-3 text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding-bottom: 16px;
  min-height: 0;
}
.permission-checklist {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 240px;
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
</style>
