<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { useUserStore } from '@/stores/user.store'
import { CoarCard, CoarButton, CoarSelect, CoarTextInput, CoarFormField, CoarIcon } from '@cocoar/vue-ui'
import { PERMISSION_RESOURCES, RESOURCE_LABELS } from '@/models/role'
import type { ExternalLinkDto } from '@/models/externalLink'

const { t, language } = useI18n()
const ui = useUI()
const http = useHttpClient('/api/admin/authorization')
const userStore = useUserStore()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.simulator.title', {}, 'Policy Simulator')
  ctx.header.icon = 'flask-conical'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

onMounted(() => userStore.loadLookup())

interface PermissionGrantTrace { GroupName: string; RoleName: string; Permission: string }
interface ScopeScriptTrace { GroupName: string; Script: string | null; Matches: boolean | null }
interface SimulatedClaimsInfo {
  Source: 'link' | 'override'
  LinkIdpName: string | null
  CapturedAt: string | null
  GroupCount: number
  RoleCount: number
  HasEmail: boolean
}
interface SimulationResult {
  Outcome: 'Allowed' | 'PermissionDenied' | 'ScopeDenied' | 'ResourceNotFound'
  RequiredPermission: string
  PermissionGranted: boolean
  AdminBypass: boolean
  PermissionTrace: PermissionGrantTrace[]
  ScopeTrace: ScopeScriptTrace[]
  RowInScope: boolean
  RowExists: boolean
  Summary: string
  SimulatedClaims: SimulatedClaimsInfo | null
}

type ClaimsMode = 'none' | 'link' | 'custom'

const form = ref({
  userId: '',
  resourceType: 'todo',
  action: 'read',
  resourceId: '',
})

// "Simulate as" — claims context for this run.
// 'none' uses no external claims (plain local session).
// 'link' pulls LastKnownClaims from one of the user's ExternalIdentityLinks.
// 'custom' lets the admin paste a hypothetical groups/roles list.
const claimsMode = ref<ClaimsMode>('none')
const selectedLinkId = ref<string>('')
const customGroupsText = ref('')
const customRolesText = ref('')
const customEmail = ref('')

const userLinks = ref<ExternalLinkDto[]>([])
const linksLoading = ref(false)

// Reload the user's external links whenever the selected user changes.
watch(() => form.value.userId, async (uid) => {
  userLinks.value = []
  selectedLinkId.value = ''
  if (!uid) return
  linksLoading.value = true
  try {
    const linkHttp = useHttpClient(`/api/admin/users/${uid}/external-links`)
    userLinks.value = await linkHttp.get<ExternalLinkDto[]>()
    // Auto-pick the most recent link if the user is in link-mode and has links
    if (claimsMode.value === 'link' && userLinks.value.length > 0) {
      selectedLinkId.value = userLinks.value[0].Id
    }
  } catch {
    userLinks.value = []
  } finally {
    linksLoading.value = false
  }
})

const linkOptions = computed(() =>
  userLinks.value
    .filter(l => l.LastKnownClaims != null)
    .map(l => {
      const captured = l.LastKnownClaims?.CapturedAt
        ? new Date(l.LastKnownClaims.CapturedAt).toLocaleDateString()
        : '?'
      return {
        value: l.Id,
        label: `${l.IdpDisplayName} · ${captured}`,
        subtitle: l.Email ?? undefined,
      }
    })
)

const claimsModeOptions = computed(() => [
  { value: 'none', label: t('admin.simulator.claims.none', {}, 'No external claims') },
  { value: 'link', label: t('admin.simulator.claims.link', {}, 'Last claims from an IdP link') },
  { value: 'custom', label: t('admin.simulator.claims.custom', {}, 'Custom claims (hypothesis)') },
])

// When switching into link mode, default to the newest link.
watch(claimsMode, (m) => {
  if (m === 'link' && !selectedLinkId.value && userLinks.value.length > 0) {
    selectedLinkId.value = userLinks.value[0].Id
  }
})

function parseLines(text: string): string[] {
  return text
    .split(/[\n,]/)
    .map(s => s.trim())
    .filter(s => s.length > 0)
}

const result = ref<SimulationResult | null>(null)
const running = ref(false)
const error = ref<string | null>(null)

// Actions for the selected resource — exclude 'app' (admin bypass doesn't need simulation)
const actionOptions = computed(() => {
  const actions = PERMISSION_RESOURCES[form.value.resourceType] ?? []
  return actions.map(a => ({ value: a, label: a }))
})

const resourceTypeOptions = computed(() =>
  Object.keys(PERMISSION_RESOURCES)
    .filter(rt => rt !== 'app')
    .map(rt => ({ value: rt, label: RESOURCE_LABELS[rt] || rt }))
)

// Reset action when resource type changes so we don't keep an incompatible one
watch(() => form.value.resourceType, () => {
  const actions = PERMISSION_RESOURCES[form.value.resourceType] ?? []
  if (!actions.includes(form.value.action)) form.value.action = actions[0] ?? 'read'
})

const userOptions = computed(() =>
  userStore.lookupEntities.map(u => {
    const name = [u.Firstname, u.Lastname].filter(Boolean).join(' ')
    const label = u.UserName || u.Label || name || u.Id
    const subtitle = [u.Acronym, name].filter(Boolean).join(' | ') || undefined
    return { value: u.Id, label, subtitle }
  })
)

const canRun = computed(() =>
  !!form.value.userId && !!form.value.resourceType && !!form.value.action && !running.value)

async function run() {
  if (!canRun.value) return
  running.value = true
  error.value = null
  result.value = null
  try {
    const payload: Record<string, unknown> = {
      UserId: form.value.userId,
      ResourceType: form.value.resourceType,
      Action: form.value.action,
      ResourceId: form.value.resourceId.trim() || null,
    }
    if (claimsMode.value === 'link' && selectedLinkId.value) {
      payload.ExternalLinkId = selectedLinkId.value
    } else if (claimsMode.value === 'custom') {
      payload.OverrideClaims = {
        Email: customEmail.value.trim() || null,
        Groups: parseLines(customGroupsText.value),
        Roles: parseLines(customRolesText.value),
      }
    }
    result.value = await http.addPath('simulate').post<SimulationResult>(payload)
  } catch (e: any) {
    error.value = e?.data?.error || e?.message || 'Simulation failed'
  } finally {
    running.value = false
  }
}

const outcomeStyle = computed(() => {
  if (!result.value) return ''
  return result.value.Outcome === 'Allowed' ? 'outcome-allowed' : 'outcome-denied'
})

const outcomeLabel = computed(() => {
  if (!result.value) return ''
  switch (result.value.Outcome) {
    case 'Allowed': return t('admin.simulator.outcome.allowed', {}, 'Allowed')
    case 'PermissionDenied': return t('admin.simulator.outcome.permissionDenied', {}, 'Permission denied')
    case 'ScopeDenied': return t('admin.simulator.outcome.scopeDenied', {}, 'Scope denied')
    case 'ResourceNotFound': return t('admin.simulator.outcome.notFound', {}, 'Resource not found')
  }
})
</script>

<template>
  <div class="simulator-page">
    <CoarCard>
      <div class="form-grid">
        <CoarFormField :label="t('admin.simulator.user', {}, 'User')">
          <CoarSelect
            v-model="form.userId"
            :options="userOptions"
            searchable
            :placeholder="t('admin.simulator.userPlaceholder', {}, 'Pick a user…')"
          />
        </CoarFormField>

        <CoarFormField :label="t('admin.simulator.resource', {}, 'Resource')">
          <CoarSelect v-model="form.resourceType" :options="resourceTypeOptions" />
        </CoarFormField>

        <CoarFormField :label="t('admin.simulator.action', {}, 'Action')">
          <CoarSelect v-model="form.action" :options="actionOptions" />
        </CoarFormField>

        <CoarFormField :label="t('admin.simulator.resourceId', {}, 'Resource ID (optional)')">
          <CoarTextInput
            v-model="form.resourceId"
            clearable
            :placeholder="t('admin.simulator.resourceIdHint', {}, 'Leave empty to check permission only')"
          />
        </CoarFormField>
      </div>

      <div class="claims-block">
        <div class="claims-header">
          <CoarIcon name="key-round" size="s" />
          <span>{{ t('admin.simulator.claims.title', {}, 'Simulate as') }}</span>
        </div>
        <div class="claims-grid">
          <CoarFormField :label="t('admin.simulator.claims.mode', {}, 'Mode')">
            <CoarSelect v-model="claimsMode" :options="claimsModeOptions" />
          </CoarFormField>

          <CoarFormField
            v-if="claimsMode === 'link'"
            :label="t('admin.simulator.claims.link', {}, 'IdP login')">
            <CoarSelect
              v-model="selectedLinkId"
              :options="linkOptions"
              :disabled="linksLoading || linkOptions.length === 0"
              :placeholder="linkOptions.length === 0
                ? t('admin.simulator.claims.noLinks', {}, 'No stored IdP logins for this user')
                : t('admin.simulator.claims.pickLink', {}, 'Pick a login…')"
            />
          </CoarFormField>
        </div>

        <div v-if="claimsMode === 'custom'" class="custom-grid">
          <CoarFormField :label="t('admin.simulator.claims.email', {}, 'Email')">
            <CoarTextInput v-model="customEmail" clearable placeholder="alice@acme.com" />
          </CoarFormField>
          <CoarFormField :label="t('admin.simulator.claims.groups', {}, 'Groups (comma or newline)')">
            <textarea v-model="customGroupsText" class="claims-textarea" rows="3"
              placeholder="Admins, Engineering" />
          </CoarFormField>
          <CoarFormField :label="t('admin.simulator.claims.roles', {}, 'Roles (comma or newline)')">
            <textarea v-model="customRolesText" class="claims-textarea" rows="3"
              placeholder="Contributor" />
          </CoarFormField>
        </div>

        <p v-if="claimsMode !== 'none'" class="claims-hint">
          {{ t('admin.simulator.claims.hint', {},
            'Scripts that read user.ExternalClaims (e.g. checking Entra group membership) will see these values instead of the user\'s current session claims.') }}
        </p>
      </div>

      <div class="run-row">
        <CoarButton variant="primary" :loading="running" :disabled="!canRun" @click="run">
          {{ t('admin.simulator.run', {}, 'Simulate') }}
        </CoarButton>
        <p class="hint">
          {{ t('admin.simulator.hint', {},
            'Explains whether a user would be allowed and which groups contribute to permission and scope.') }}
        </p>
      </div>
    </CoarCard>

    <div v-if="error" class="error-card">
      <CoarIcon name="alert-triangle" size="s" />
      <span>{{ error }}</span>
    </div>

    <CoarCard v-if="result" class="result-card">
      <div class="outcome-row" :class="outcomeStyle">
        <CoarIcon :name="result.Outcome === 'Allowed' ? 'circle-check' : 'ban'" size="m" />
        <div class="outcome-body">
          <div class="outcome-title">{{ outcomeLabel }}</div>
          <div class="outcome-summary">{{ result.Summary }}</div>
          <div v-if="result.SimulatedClaims" class="outcome-claims-note">
            <CoarIcon name="key-round" size="xs" />
            <span v-if="result.SimulatedClaims.Source === 'link'">
              {{ t('admin.simulator.claims.evaluatedAsLink', {}, 'Evaluated as if logged in via') }}
              <strong>{{ result.SimulatedClaims.LinkIdpName }}</strong>
              <template v-if="result.SimulatedClaims.CapturedAt">
                ({{ new Date(result.SimulatedClaims.CapturedAt).toLocaleString() }})
              </template>
              —
              {{ result.SimulatedClaims.GroupCount }} {{ t('admin.simulator.claims.groups', {}, 'Groups') }},
              {{ result.SimulatedClaims.RoleCount }} {{ t('admin.simulator.claims.roles', {}, 'Roles') }}
            </span>
            <span v-else>
              {{ t('admin.simulator.claims.evaluatedAsOverride', {}, 'Evaluated with custom claims') }} —
              {{ result.SimulatedClaims.GroupCount }} {{ t('admin.simulator.claims.groups', {}, 'Groups') }},
              {{ result.SimulatedClaims.RoleCount }} {{ t('admin.simulator.claims.roles', {}, 'Roles') }}
            </span>
          </div>
        </div>
      </div>

      <div class="section">
        <div class="section-title">{{ t('admin.simulator.permission', {}, 'Permission') }}</div>
        <dl class="kv">
          <dt>{{ t('admin.simulator.required', {}, 'Required') }}</dt>
          <dd><code>{{ result.RequiredPermission }}</code></dd>
          <dt>{{ t('admin.simulator.granted', {}, 'Granted') }}</dt>
          <dd>
            <span :class="result.PermissionGranted ? 'pill-ok' : 'pill-bad'">
              {{ result.PermissionGranted ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}
            </span>
            <span v-if="result.AdminBypass" class="pill-info">
              {{ t('admin.simulator.adminBypass', {}, 'admin bypass') }}
            </span>
          </dd>
        </dl>

        <div v-if="result.PermissionTrace.length > 0" class="trace-table">
          <div class="trace-head">
            <span>{{ t('admin.simulator.group', {}, 'Group') }}</span>
            <span>{{ t('admin.simulator.role', {}, 'Role') }}</span>
            <span>{{ t('admin.simulator.permission', {}, 'Permission') }}</span>
          </div>
          <div v-for="(t_, i) in result.PermissionTrace" :key="i" class="trace-row">
            <span>{{ t_.GroupName }}</span>
            <span>{{ t_.RoleName }}</span>
            <span><code>{{ t_.Permission }}</code></span>
          </div>
        </div>
        <div v-else-if="!result.AdminBypass" class="empty">
          {{ t('admin.simulator.noGrant', {}, 'No group grants the required permission.') }}
        </div>
      </div>

      <div v-if="!result.AdminBypass && result.PermissionGranted" class="section">
        <div class="section-title">{{ t('admin.simulator.scope', {}, 'Scope') }}</div>
        <dl class="kv">
          <dt>{{ t('admin.simulator.rowExists', {}, 'Row exists') }}</dt>
          <dd>
            <span :class="result.RowExists ? 'pill-ok' : 'pill-bad'">
              {{ result.RowExists ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}
            </span>
          </dd>
          <dt>{{ t('admin.simulator.rowInScope', {}, 'Row in scope') }}</dt>
          <dd>
            <span :class="result.RowInScope ? 'pill-ok' : 'pill-bad'">
              {{ result.RowInScope ? t('common.yes', {}, 'Yes') : t('common.no', {}, 'No') }}
            </span>
          </dd>
        </dl>

        <div v-if="result.ScopeTrace.length > 0" class="trace-table">
          <div class="trace-head">
            <span>{{ t('admin.simulator.group', {}, 'Group') }}</span>
            <span>{{ t('admin.simulator.script', {}, 'Access script') }}</span>
          </div>
          <div v-for="(s, i) in result.ScopeTrace" :key="i" class="trace-row trace-row-wide">
            <span>{{ s.GroupName }}</span>
            <span>
              <code v-if="s.Script">{{ s.Script }}</code>
              <em v-else class="empty">{{ t('admin.simulator.noScript', {}, '(no access script → unrestricted)') }}</em>
            </span>
          </div>
        </div>
        <div v-else class="empty">
          {{ t('admin.simulator.noContributingGroups', {}, 'No group contributes scope for this action.') }}
        </div>
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.simulator-page {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}

.claims-block {
  margin-top: 16px;
  padding: 12px;
  border: 1px dashed #d1d5db;
  border-radius: 6px;
  background: var(--coar-background-neutral-subtle, #fafbfc);
}

.claims-header {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.8rem;
  font-weight: 600;
  color: #525e76;
  margin-bottom: 8px;
}

.claims-grid {
  display: grid;
  grid-template-columns: 220px 1fr;
  gap: 12px;
}

.custom-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
  margin-top: 8px;
}

.claims-textarea {
  width: 100%;
  padding: 6px 8px;
  border: 1px solid #d1d5db;
  border-radius: 4px;
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
  resize: vertical;
  background: var(--coar-background, #fff);
  color: inherit;
}

.claims-hint {
  margin: 8px 0 0;
  font-size: 0.72rem;
  color: var(--coar-text-muted, #6b7280);
  font-style: italic;
}

.outcome-claims-note {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: 4px;
  font-size: 0.72rem;
  color: var(--coar-text-muted, #6b7280);
}

.run-row {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}

.hint {
  font-size: 0.75rem;
  color: var(--coar-text-muted, #6b7280);
  margin: 0;
  flex: 1;
}

.error-card {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: var(--coar-background-danger-subtle, #fef2f2);
  border: 1px solid var(--coar-border-danger, #fca5a5);
  color: var(--coar-text-danger, #b91c1c);
  border-radius: 4px;
  font-size: 0.8rem;
}

.result-card {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.outcome-row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  border-radius: 6px;
  border: 1px solid transparent;
}

.outcome-allowed {
  background: var(--coar-background-success-subtle, #ecfdf5);
  border-color: var(--coar-border-success, #86efac);
  color: var(--coar-text-success, #065f46);
}

.outcome-denied {
  background: var(--coar-background-danger-subtle, #fef2f2);
  border-color: var(--coar-border-danger, #fca5a5);
  color: var(--coar-text-danger, #b91c1c);
}

.outcome-title {
  font-weight: 600;
  font-size: 0.95rem;
}

.outcome-summary {
  font-size: 0.8rem;
  margin-top: 2px;
  color: var(--coar-text, #374151);
}

.section-title {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  border-bottom: 1px solid #d1d5db;
  padding-bottom: 4px;
  margin-bottom: 8px;
}

.kv {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 16px;
  margin: 0 0 12px 0;
  font-size: 0.8rem;
}

.kv dt {
  color: var(--coar-text-muted, #6b7280);
  font-weight: 500;
}

.kv dd {
  margin: 0;
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}

.pill-ok, .pill-bad, .pill-info {
  font-size: 0.7rem;
  padding: 1px 6px;
  border-radius: 999px;
  font-weight: 500;
}

.pill-ok {
  background: var(--coar-background-success-subtle, #ecfdf5);
  color: var(--coar-text-success, #065f46);
}

.pill-bad {
  background: var(--coar-background-danger-subtle, #fef2f2);
  color: var(--coar-text-danger, #b91c1c);
}

.pill-info {
  background: var(--coar-background-info-subtle, #eff6ff);
  color: var(--coar-text-info, #1d4ed8);
}

.trace-table {
  border: 1px solid #e5e7eb;
  border-radius: 4px;
  font-size: 0.8rem;
}

.trace-head, .trace-row {
  display: grid;
  grid-template-columns: 1fr 1fr 1fr;
  gap: 8px;
  padding: 6px 10px;
  align-items: center;
}

.trace-row-wide {
  grid-template-columns: 1fr 2fr;
}

.trace-head {
  background: var(--coar-background-neutral-secondary, #f9fafb);
  font-weight: 600;
  color: #525e76;
  border-bottom: 1px solid #e5e7eb;
}

.trace-row + .trace-row {
  border-top: 1px solid #f3f4f6;
}

.empty {
  font-size: 0.75rem;
  color: var(--coar-text-muted, #6b7280);
  font-style: italic;
  padding: 6px 0;
}

code {
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
  padding: 1px 4px;
  background: var(--coar-background-neutral-secondary, #f3f4f6);
  border-radius: 3px;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
