<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { usePositionStore } from '@/stores/position.store'
import {
  CoarNotice,
  CoarTextInput,
  CoarNumberInput,
  CoarFormField,
  CoarCheckbox,
  CoarDivider,
  CoarSelect,
  CoarButton,
  CoarTag,
  CoarPopconfirm,
  CoarTabGroup,
  CoarTab,
  CoarIcon,
  CoarPopover,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import { useUserStore } from '@/stores/user.store'
import type { PositionCreateDto, PositionUpdateDto, PositionTerminalPolicyUpdateDto, PositionTerminalPolicyConsequencesDto, PositionGrantDto, TerminalDto, StaffingSessionDto, ActivationTokenDto } from '@/models/position'
import type { RealmSettingsDto } from '@/models/realmSettings'

const { t } = useI18n()
const toast = useToast()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
  /**
   * Embedded create mode used by the OAuth-client editor. The position is
   * returned as a draft and committed atomically with its first terminal
   * client, mirroring the ServiceAccount draft flow.
   */
  draftOnly?: boolean
  initial?: PositionCreateDto
}>()

const store = usePositionStore()
const userStore = useUserStore()
const isCreate = computed(() => props.id === 'create')

// ── ADR-0005 staging: position identity + policy + grants commit onto the
// active draft. Terminal SLOTS are deliberately not manifest-modeled
// (credential material), so a create that stages slots takes the live path;
// the embedded draftOnly flow belongs to the parent client create and stays
// untouched. Grant/slot/session OPERATIONS in edit mode remain live actions.
const staging = useDraftStaging('positions')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value
  && !props.draftOnly
  && !(isCreate.value && stagedTerminals.value.length > 0))
const loading = ref(false)
const error = ref<string | null>(null)
const creationCompleted = ref(false)
const createdTerminalSecrets = ref<{ ClientId: string; ClientSecret: string }[]>([])
// Modal-contract rule 5: create and edit share the layout — the sessions tab
// is simply absent while the position does not exist yet.
const activeTab = ref<'general' | 'terminals' | 'grants' | 'tokens' | 'sessions'>('general')

const form = ref({
  AccountName: props.initial?.AccountName ?? '',
  Purpose: props.initial?.Purpose ?? '',
  IsActive: props.initial?.IsActive ?? true,
  // An embedded draft is specifically being created for a terminal client,
  // so terminal use starts enabled and cannot accidentally be switched off.
  TerminalEnabled: props.initial?.TerminalPolicy?.Enabled ?? (props.draftOnly ? true : false),
  AllowedActivationProofs: [...(props.initial?.TerminalPolicy?.AllowedActivationProofs ?? ['personal-passkey'])] as string[],
  AllowedDeviceBindings: [...(props.initial?.TerminalPolicy?.AllowedDeviceBindings ?? ['dpop'])] as string[],
  // Plan defaults: a 16 h shift session under a 24 h absolute ceiling.
  StaffingSessionLifetimeMinutes: props.initial?.TerminalPolicy?.StaffingSessionLifetimeMinutes ?? 16 * 60,
  MaximumStaffingSessionLifetimeMinutes: props.initial?.TerminalPolicy?.MaximumStaffingSessionLifetimeMinutes ?? 24 * 60,
})
const original = ref({ ...form.value })
const realmPositionSecurity = ref<RealmSettingsDto['PositionSecurity'] | null>(null)
const activationProofOptions = computed(() => [
  { id: 'personal-passkey', label: t('admin.positions.activationProof.personalPasskey', {}, 'Personal passkey'), available: true, phase: '' },
  { id: 'personal-password', label: t('admin.positions.activationProof.personalPassword', {}, 'Personal password'), available: true, phase: '' },
  { id: 'personal-email-otp', label: t('admin.positions.activationProof.personalEmailOtp', {}, 'Personal email OTP'), available: true, phase: '' },
  { id: 'position-token', label: t('admin.positions.activationProof.positionToken', {}, 'Position token'), available: true, phase: '' },
  { id: 'team-secret', label: t('admin.positions.activationProof.teamSecret', {}, 'Team secret'), available: false, phase: 'reserved' },
] as const)
const deviceBindingOptions = computed(() => [
  { id: 'dpop', label: t('admin.positionTerminals.bindingOption.dpop.label', {}, 'DPoP key'), available: true, phase: '' },
  { id: 'client-secret', label: t('admin.positionTerminals.bindingOption.clientSecret.label', {}, 'Client secret'), available: true, phase: '' },
  { id: 'none', label: t('admin.positionTerminals.bindingOption.none.label', {}, 'No device binding'), available: true, phase: '' },
] as const)
const terminalBindingSelectOptions = computed(() => deviceBindingOptions.value
  .filter((option) => option.available && form.value.AllowedDeviceBindings.includes(option.id))
  .map((option) => ({ value: option.id, label: option.label })))

function setAllowed(collection: 'AllowedActivationProofs' | 'AllowedDeviceBindings', id: string, enabled: boolean) {
  const values = form.value[collection]
  form.value[collection] = enabled
    ? Array.from(new Set([...values, id]))
    : values.filter((value) => value !== id)
}
const accountNamePattern = /^[a-z0-9][a-z0-9._-]{1,63}$/

/** Loads the staged manifest entity into the form. */
function fromStagedInto(e: ManifestEntity) {
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const arr = (v: unknown, fallback: string[]) => (Array.isArray(v) && v.length > 0 ? [...(v as string[])] : fallback)
  const num = (v: unknown, fallback: number) => (typeof v === 'number' ? v : fallback)
  const policy = (e.TerminalPolicy ?? {}) as ManifestEntity
  form.value = {
    AccountName: str(e.AccountName),
    Purpose: str(e.Purpose),
    IsActive: e.IsActive !== false,
    TerminalEnabled: policy.Enabled === true,
    AllowedActivationProofs: arr(policy.AllowedActivationProofs, ['personal-passkey']),
    AllowedDeviceBindings: arr(policy.AllowedDeviceBindings, ['dpop']),
    StaffingSessionLifetimeMinutes: num(policy.StaffingSessionLifetimeMinutes, 16 * 60),
    MaximumStaffingSessionLifetimeMinutes: num(policy.MaximumStaffingSessionLifetimeMinutes, 24 * 60),
  }
  original.value = { ...form.value }
}

function toStaged(): ManifestEntity {
  const name = form.value.AccountName.trim()
  // The section's natural key is the lowercased account name.
  const entity: ManifestEntity = { ...(staging.findStaged(name.toLowerCase()) ?? {}) }
  entity.AccountName = name
  entity.IsActive = form.value.IsActive
  // v2 merge-patch: explicit null stages the clear (absent would keep live).
  entity.Purpose = form.value.Purpose.trim() || null
  entity.TerminalPolicy = {
    Enabled: form.value.TerminalEnabled,
    AllowedActivationProofs: [...form.value.AllowedActivationProofs],
    AllowedDeviceBindings: [...form.value.AllowedDeviceBindings],
    StaffingSessionLifetimeMinutes: form.value.StaffingSessionLifetimeMinutes,
    MaximumStaffingSessionLifetimeMinutes: form.value.MaximumStaffingSessionLifetimeMinutes,
  }
  // Staged CREATE carries its staged grants as user keys; on edits the merge
  // base keeps whatever the draft already holds (grant ops stay live).
  if (isCreate.value && stagedGrantUserIds.value.length > 0) {
    entity.Grants = stagedGrantUserIds.value
      .map((id) => {
        const u = userStore.entities.find((x) => x.Id === id)
        return u ? (u.UserName || u.Email || null) : null
      })
      .filter((key): key is string => !!key)
  }
  // Stage the LIVE entity's id: the apply matches by identity, so editing the name
  // is a RENAME of this entity instead of staging a second one.
  if (!isCreate.value && !isDraftRow.value) entity.Id = props.id
  return entity
}

const accountNameError = computed(() => {
  const value = form.value.AccountName.trim()
  if (!value || !isCreate.value) return ''
  if (!accountNamePattern.test(value))
    return t(
      'admin.positions.accountNameInvalid',
      {},
      '2–64 characters; only lowercase letters, digits, dot, hyphen, and underscore.',
    )
  return ''
})

const lifetimeError = computed(() => {
  const session = form.value.StaffingSessionLifetimeMinutes
  const maximum = form.value.MaximumStaffingSessionLifetimeMinutes
  if (session <= 0 || maximum <= 0)
    return t('admin.positions.lifetimePositive', {}, 'Lifetimes must be positive.')
  if (session > maximum)
    return t('admin.positions.lifetimeCeiling', {}, 'The session lifetime must not exceed the absolute maximum.')
  return ''
})

const modalTitle = computed(() => {
  if (props.draftOnly)
    return t('admin.positions.createTerminalPositionTitle', {}, 'Neue Position für Terminal')
  return isCreate.value
    ? t('admin.positions.createTitle', {}, 'Create position')
    : (form.value.AccountName || t('admin.positions.editTitle', {}, 'Position'))
})

const footerButton = computed(() => ({
  visible: !creationCompleted.value,
  text: props.draftOnly
    ? t('common.apply', {}, 'Übernehmen')
    : stagedSave.value
      ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
      : isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.AccountName.trim() || generalIssues.value.length > 0
    || terminalIssues.value.length > 0 || loading.value,
  onClick: save,
}))

// ── Activation grants (MG-FT-02) — edit-mode only. Operations, not staged
// edits (modal-contract rule 2): grants have their own lifecycle and audit
// identity, mirroring the SA credentials tab, so issue/suspend/resume/revoke
// act immediately with explicit buttons, apart from the primary Save.
const grants = ref<PositionGrantDto[]>([])
const grantsLoading = ref(false)
const selectedGrantUserId = ref<string | null>(null)
const grantsHttp = computed(() => useHttpClient(`/api/position/${props.id}/grants`))

// Create mode stages grants (rule 5: the entity is creatable completely — the
// one Save commits position + grants atomically); edit mode operates on live
// grants immediately (rule 2, they have their own lifecycle + audit identity).
const stagedGrantUserIds = ref<string[]>([])

function userLabel(userId: string): string {
  const u = userStore.entities.find((x) => x.Id === userId)
  return u ? (`${u.Firstname ?? ''} ${u.Lastname ?? ''}`.trim() || u.Email || userId) : userId
}

const grantableUserOptions = computed(() => {
  const taken = isCreate.value
    ? new Set(stagedGrantUserIds.value)
    : new Set(grants.value.filter((g) => g.Status !== 'Revoked').map((g) => g.UserId))
  return userStore.entities
    .filter((u) => u.IsActive && !taken.has(u.Id))
    .map((u) => ({ value: u.Id, label: userLabel(u.Id) }))
})

function stageGrant() {
  if (!selectedGrantUserId.value) return
  stagedGrantUserIds.value.push(selectedGrantUserId.value)
  selectedGrantUserId.value = null
}

function unstageGrant(userId: string) {
  stagedGrantUserIds.value = stagedGrantUserIds.value.filter((id) => id !== userId)
}

async function loadGrants() {
  if (isCreate.value) return
  grantsLoading.value = true
  try {
    grants.value = await grantsHttp.value.get<PositionGrantDto[]>()
  } finally {
    grantsLoading.value = false
  }
}

async function issueGrant() {
  if (!selectedGrantUserId.value) return
  try {
    await grantsHttp.value.post({ UserId: selectedGrantUserId.value })
    selectedGrantUserId.value = null
    await loadGrants()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function transitionGrant(grant: PositionGrantDto, action: 'suspend' | 'resume' | 'revoke') {
  try {
    await grantsHttp.value.addPath(grant.Id, action).post()
    await loadGrants()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

// ── Terminal slots (MG-FT-03). Create mode STAGES slots the same way it
// stages grants (rule 5: the entity is creatable completely — mirrors the
// service account's initial credential, which is staged into the same
// atomic create). Edit mode operates on live slots immediately (rule 2),
// where the PERSISTED policy has to allow them.
const terminals = ref<TerminalDto[]>([])
const terminalsLoading = ref(false)
const newTerminal = ref({
  DisplayName: '',
  Location: '',
  WebAuthnRpId: '',
  Binding: 'dpop',
  AllowedPositionIds: (isCreate.value ? [] : [props.id]) as string[],
})
const terminalsHttp = computed(() => useHttpClient(`/api/position/${props.id}/terminals`))
const stagedTerminals = ref<{
  DisplayName: string
  Location: string
  WebAuthnRpId: string
  Binding: string
  AllowedPositionIds: string[]
}[]>([])
const revealedTerminalSecret = ref<string | null>(null)
const editingTerminalPositionsId = ref<string | null>(null)
const terminalPositionDrafts = ref<Record<string, string[]>>({})
// In create the staged policy decides (it is committed in the same save); in
// edit only the persisted one does, because the server validates against it.
const canAddTerminal = computed(() =>
  isCreate.value ? form.value.TerminalEnabled : original.value.TerminalEnabled)

// Passkeys hang off the RP ID, not the client: the existing hardware tokens of
// this position unlock a new slot only when it carries the SAME RP ID. So once
// any slot exists (live or staged), further slots inherit its RP ID and the
// field locks.
const existingRpId = computed(() =>
  terminals.value.find((slot) => slot.Status !== 'Revoked')?.WebAuthnRpId
  ?? stagedTerminals.value[0]?.WebAuthnRpId
  ?? '')

watch(existingRpId, (rpId) => {
  if (rpId) newTerminal.value.WebAuthnRpId = rpId
}, { immediate: true })

watch(terminalBindingSelectOptions, (options) => {
  if (!options.some((option) => option.value === newTerminal.value.Binding))
    newTerminal.value.Binding = options[0]?.value ?? ''
})

const compatibleAdditionalPositions = computed(() => store.entities
  .filter((position) => position.Id !== props.id
    && position.IsActive
    && position.TerminalPolicy.Enabled
    && position.TerminalPolicy.AllowedDeviceBindings.includes(newTerminal.value.Binding))
  .sort((left, right) => left.AccountName.localeCompare(right.AccountName)))

function setNewTerminalPosition(positionId: string, enabled: boolean) {
  newTerminal.value.AllowedPositionIds = enabled
    ? Array.from(new Set([...newTerminal.value.AllowedPositionIds, positionId]))
    : newTerminal.value.AllowedPositionIds.filter((id) => id !== positionId)
}

function stageTerminal() {
  if (!newTerminal.value.DisplayName.trim() || !newTerminal.value.WebAuthnRpId.trim()) return
  stagedTerminals.value.push({
    DisplayName: newTerminal.value.DisplayName.trim(),
    Location: newTerminal.value.Location.trim(),
    WebAuthnRpId: newTerminal.value.WebAuthnRpId.trim(),
    Binding: newTerminal.value.Binding,
    AllowedPositionIds: [...newTerminal.value.AllowedPositionIds],
  })
  // Keep the RP ID — every terminal of one consuming app shares it.
  newTerminal.value = {
    DisplayName: '', Location: '', WebAuthnRpId: newTerminal.value.WebAuthnRpId,
    Binding: 'dpop', AllowedPositionIds: isCreate.value ? [] : [props.id],
  }
}

function unstageTerminal(index: number) {
  stagedTerminals.value.splice(index, 1)
}

// Tabs organize, they don't disclose (rule 1) — so a validation error on an
// inactive tab is flagged on its label, otherwise a disabled Save would have
// no visible cause. Same shape as GroupDetails.
const generalIssues = computed(() => [accountNameError.value].filter(Boolean) as string[])
const terminalIssues = computed(() => {
  const issues = [lifetimeError.value].filter(Boolean) as string[]
  if (form.value.TerminalEnabled && form.value.AllowedActivationProofs.length === 0)
    issues.push(t('admin.positions.activationProofRequired', {}, 'Select at least one activation proof.'))
  if (form.value.TerminalEnabled && form.value.AllowedDeviceBindings.length === 0)
    issues.push(t('admin.positions.deviceBindingRequired', {}, 'Select at least one device binding.'))
  if (stagedTerminals.value.length > 0 && !form.value.TerminalEnabled)
    issues.push(t('admin.positionTerminals.stagedNeedPolicy', {},
      'Turn terminal use on — the staged slots are saved with it.'))
  return issues
})

async function loadTerminals() {
  if (isCreate.value) return
  terminalsLoading.value = true
  try {
    terminals.value = await terminalsHttp.value.get<TerminalDto[]>()
  } finally {
    terminalsLoading.value = false
  }
}

async function createTerminal() {
  if (!newTerminal.value.DisplayName.trim() || !newTerminal.value.WebAuthnRpId.trim()) return
  try {
    const created = await terminalsHttp.value.post<TerminalDto>({
      DisplayName: newTerminal.value.DisplayName.trim(),
      Location: newTerminal.value.Location.trim() || undefined,
      WebAuthnRpId: newTerminal.value.WebAuthnRpId.trim(),
      Binding: newTerminal.value.Binding,
      AllowedPositionIds: [...newTerminal.value.AllowedPositionIds],
    })
    revealedTerminalSecret.value = created.ClientSecret ?? null
    newTerminal.value = {
      DisplayName: '', Location: '', WebAuthnRpId: newTerminal.value.WebAuthnRpId,
      Binding: 'dpop', AllowedPositionIds: [props.id],
    }
    await loadTerminals()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function copyTerminalSecret() {
  if (revealedTerminalSecret.value) await navigator.clipboard.writeText(revealedTerminalSecret.value)
}
async function copyText(value: string) { await navigator.clipboard.writeText(value) }

function positionLabel(positionId: string): string {
  return store.entities.find((position) => position.Id === positionId)?.AccountName ?? positionId
}

function editTerminalPositions(terminal: TerminalDto) {
  editingTerminalPositionsId.value = terminal.Id
  terminalPositionDrafts.value[terminal.Id] = [...terminal.AllowedPositionIds]
}

function setTerminalPosition(terminal: TerminalDto, positionId: string, enabled: boolean) {
  const current = terminalPositionDrafts.value[terminal.Id] ?? [...terminal.AllowedPositionIds]
  terminalPositionDrafts.value[terminal.Id] = enabled
    ? Array.from(new Set([...current, positionId]))
    : current.filter((id) => id !== positionId)
}

function positionsForTerminal(terminal: TerminalDto) {
  return store.entities
    .filter((position) => terminal.AllowedPositionIds.includes(position.Id)
      || (position.IsActive
        && position.TerminalPolicy.Enabled
        && position.TerminalPolicy.AllowedDeviceBindings.includes(terminal.Binding)))
    .sort((left, right) => left.AccountName.localeCompare(right.AccountName))
}

async function saveTerminalPositions(terminal: TerminalDto) {
  const allowed = terminalPositionDrafts.value[terminal.Id] ?? terminal.AllowedPositionIds
  if (allowed.length === 0) return
  try {
    await terminalsHttp.value.addPath(terminal.Id, 'positions').put({ AllowedPositionIds: allowed })
    editingTerminalPositionsId.value = null
    await loadTerminals()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

// ── Position-owned activation tokens (F2). Registration itself happens on
// an enrolled terminal so the browser origin is compatible with its RP ID.
const activationTokens = ref<ActivationTokenDto[]>([])
const activationTokensLoading = ref(false)
const newActivationTokenLabel = ref('')
const activationTokensHttp = computed(() => useHttpClient(`/api/position/${props.id}/activation-tokens`))

async function loadActivationTokens() {
  if (isCreate.value) return
  activationTokensLoading.value = true
  try { activationTokens.value = await activationTokensHttp.value.get<ActivationTokenDto[]>() }
  finally { activationTokensLoading.value = false }
}

async function createActivationToken() {
  const label = newActivationTokenLabel.value.trim()
  if (!label) return
  try {
    await activationTokensHttp.value.post({ Label: label })
    newActivationTokenLabel.value = ''
    await loadActivationTokens()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function transitionActivationToken(token: ActivationTokenDto, action: 'disable' | 'reactivate' | 'revoke') {
  try {
    await useHttpClient(`/api/activation-token/${token.Id}/${action}`).post()
    await Promise.all([loadActivationTokens(), loadStaffingSessions()])
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function transitionTerminal(terminal: TerminalDto, action: 'disable' | 'reactivate' | 'revoke') {
  try {
    await terminalsHttp.value.addPath(terminal.Id, action).post()
    await loadTerminals()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

// ── Staffing sessions (MG-FT-05/07) — read + force-lock, edit-mode only.
// The list is the admin's live view of who staffed which terminal when;
// force-lock ends a running shift remotely (§15.3).
const staffingSessions = ref<StaffingSessionDto[]>([])
const sessionsLoading = ref(false)
const sessionsHttp = computed(() => useHttpClient(`/api/position/${props.id}/staffing-sessions`))

async function loadStaffingSessions() {
  if (isCreate.value) return
  sessionsLoading.value = true
  try {
    staffingSessions.value = await sessionsHttp.value.get<StaffingSessionDto[]>()
  } catch {
    // staffing-session:read may be missing — the section simply
    // stays empty rather than breaking the modal.
    staffingSessions.value = []
  } finally {
    sessionsLoading.value = false
  }
}

async function forceLockSession(sessionId: string) {
  try {
    await useHttpClient(`/api/staffing-session/${sessionId}/force-lock`).post()
    await Promise.all([loadStaffingSessions(), loadTerminals()])
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

function terminalLabel(terminalId: string): string {
  return terminals.value.find((x) => x.Id === terminalId)?.DisplayName ?? terminalId
}

function endReasonLabel(reason: string | null | undefined): string {
  switch (reason) {
    case 'LocalLock': return t('admin.staffingSessions.reason.localLock', {}, 'Locked at the terminal')
    case 'RemoteLock': return t('admin.staffingSessions.reason.remoteLock', {}, 'Force-locked by an admin')
    case 'ReplacedByNewActivation': return t('admin.staffingSessions.reason.replaced', {}, 'Replaced by a new tap')
    case 'Expired': return t('admin.staffingSessions.reason.expired', {}, 'Shift ceiling reached')
    case 'PositionDisabled': return t('admin.staffingSessions.reason.positionDisabled', {}, 'Position deactivated')
    case 'TerminalDisabled': return t('admin.staffingSessions.reason.terminalDisabled', {}, 'Terminal disabled')
    case 'TerminalRevoked': return t('admin.staffingSessions.reason.terminalRevoked', {}, 'Terminal revoked')
    case 'UserDisabled': return t('admin.staffingSessions.reason.userDisabled', {}, 'User deactivated')
    case 'PasskeyDeleted': return t('admin.staffingSessions.reason.passkeyDeleted', {}, 'Passkey deleted')
    case 'GrantSuspended': return t('admin.staffingSessions.reason.grantSuspended', {}, 'Grant suspended')
    case 'GrantRevoked': return t('admin.staffingSessions.reason.grantRevoked', {}, 'Grant revoked')
    case 'OAuthClientDisabled': return t('admin.staffingSessions.reason.clientDisabled', {}, 'OAuth client disabled')
    case 'PolicyTightened': return t('admin.staffingSessions.reason.policyTightened', {}, 'Security policy tightened')
    case 'ActivationCredentialInvalidated': return t('admin.staffingSessions.reason.credentialInvalidated', {}, 'Activation credential invalidated')
    default: return reason ?? ''
  }
}

function formatTime(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
}

onMounted(async () => {
  // Draft mode only configures the new position's identity + policy. It does
  // not need live users or positions because grants/extra slots belong to the
  // parent create operation.
  if (!props.draftOnly) {
    // The user list feeds the grant picker in BOTH modes.
    void userStore.initialize()
    // n:m terminal assignment needs the complete position catalog in both modes.
    void store.loadAll()
  }
  void useHttpClient('/api/admin/realm-settings').get<RealmSettingsDto>()
    .then((dto) => { realmPositionSecurity.value = dto.PositionSecurity })
    .catch(() => { realmPositionSecurity.value = null })
  if (isDraftRow.value) {
    // Draft-created position: the staged manifest entity IS the state; the
    // operational tabs (slots/grants/sessions) exist only after apply.
    const entity = staging.findStaged(staging.draftKeyOf(props.id))
    if (entity) fromStagedInto(entity)
    return
  }
  if (!isCreate.value) {
    loading.value = true
    try {
      const fn = await store.getById(props.id)
      form.value = {
        AccountName: fn.AccountName,
        Purpose: fn.Purpose ?? '',
        IsActive: fn.IsActive,
        TerminalEnabled: fn.TerminalPolicy.Enabled,
        AllowedActivationProofs: [...(fn.TerminalPolicy.AllowedActivationProofs ?? ['personal-passkey'])],
        AllowedDeviceBindings: [...(fn.TerminalPolicy.AllowedDeviceBindings ?? ['dpop'])],
        StaffingSessionLifetimeMinutes: fn.TerminalPolicy.StaffingSessionLifetimeMinutes,
        MaximumStaffingSessionLifetimeMinutes: fn.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes,
      }
      original.value = { ...form.value }
      // Staging overlay: show the STAGED position state when the draft carries it.
      if (stagedSave.value && staging.draftStore.current) {
        const entity = staging.findStaged(fn.AccountName.trim().toLowerCase())
        if (entity) fromStagedInto(entity)
      }
      // Grants + terminals + sessions load alongside — they must not block the form fields.
      void loadGrants()
      void loadTerminals()
      void loadStaffingSessions()
      void loadActivationTokens()
    } catch (e: unknown) {
      const err = e as { data?: { Message?: string }; message?: string }
      error.value = err?.data?.Message ?? err?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

function policyDiff(): PositionTerminalPolicyUpdateDto | undefined {
  const diff: PositionTerminalPolicyUpdateDto = {}
  if (form.value.TerminalEnabled !== original.value.TerminalEnabled)
    diff.Enabled = form.value.TerminalEnabled
  if (form.value.AllowedActivationProofs.join('\0') !== original.value.AllowedActivationProofs.join('\0'))
    diff.AllowedActivationProofs = [...form.value.AllowedActivationProofs]
  if (form.value.AllowedDeviceBindings.join('\0') !== original.value.AllowedDeviceBindings.join('\0'))
    diff.AllowedDeviceBindings = [...form.value.AllowedDeviceBindings]
  if (form.value.StaffingSessionLifetimeMinutes !== original.value.StaffingSessionLifetimeMinutes)
    diff.StaffingSessionLifetimeMinutes = form.value.StaffingSessionLifetimeMinutes
  if (form.value.MaximumStaffingSessionLifetimeMinutes !== original.value.MaximumStaffingSessionLifetimeMinutes)
    diff.MaximumStaffingSessionLifetimeMinutes = form.value.MaximumStaffingSessionLifetimeMinutes
  return Object.keys(diff).length > 0 ? diff : undefined
}

async function save() {
  if (!form.value.AccountName.trim() || accountNameError.value || lifetimeError.value) return
  if (props.draftOnly) {
    const draft: PositionCreateDto = {
      AccountName: form.value.AccountName.trim(),
      Purpose: form.value.Purpose.trim() || undefined,
      IsActive: true,
      TerminalPolicy: {
        Enabled: true,
        AllowedActivationProofs: [...form.value.AllowedActivationProofs],
        AllowedDeviceBindings: [...form.value.AllowedDeviceBindings],
        StaffingSessionLifetimeMinutes: form.value.StaffingSessionLifetimeMinutes,
        MaximumStaffingSessionLifetimeMinutes: form.value.MaximumStaffingSessionLifetimeMinutes,
      },
    }
    props.close(draft)
    return
  }
  loading.value = true
  error.value = null
  try {
    // ADR-0005: commit onto the active draft instead of writing live. Policy
    // consequences (ended sessions, disabled slots) happen at APPLY — the plan
    // shows the change; no live preview here.
    if (stagedSave.value) {
      await staging.stage(form.value.AccountName.trim().toLowerCase(), toStaged())
      props.close()
      return
    }
    if (isCreate.value) {
      const createDto: PositionCreateDto = {
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
        IsActive: form.value.IsActive,
        TerminalPolicy: policyDiff(),
        GrantUserIds: stagedGrantUserIds.value.length > 0 ? stagedGrantUserIds.value : undefined,
        Terminals: stagedTerminals.value.length > 0
          ? stagedTerminals.value.map((slot) => ({
              DisplayName: slot.DisplayName,
              Location: slot.Location || undefined,
              WebAuthnRpId: slot.WebAuthnRpId,
              Binding: slot.Binding,
              AllowedPositionIds: slot.AllowedPositionIds.length > 0 ? slot.AllowedPositionIds : undefined,
            }))
          : undefined,
      }
      const created = await store.httpClient.post<import('@/models/position').PositionPrincipalDto>(createDto)
      store.setStoreEntities([created])
      createdTerminalSecrets.value = (created.CreatedTerminals ?? [])
        .filter((terminal) => !!terminal.ClientSecret)
        .map((terminal) => ({ ClientId: terminal.ClientId, ClientSecret: terminal.ClientSecret! }))
      if (createdTerminalSecrets.value.length > 0) {
        creationCompleted.value = true
        return
      }
    } else {
      // Send only fields that actually changed. Empty Purpose = explicit clear
      // (server normalises blank to null).
      const body: PositionUpdateDto = {
        Purpose: form.value.Purpose.trim() === '' ? null : form.value.Purpose.trim(),
      }
      if (form.value.AccountName.trim() !== original.value.AccountName) {
        body.AccountName = form.value.AccountName.trim()
      }
      if (form.value.IsActive !== original.value.IsActive) {
        body.IsActive = form.value.IsActive
      }
      const diff = policyDiff()
      if (diff) {
        body.TerminalPolicy = diff
        const consequences = await useHttpClient(`/api/position/${props.id}/terminal-policy/preview`)
          .post<PositionTerminalPolicyConsequencesDto>(diff)
        if (consequences.HasConsequences) {
          const confirmed = confirm(t(
            'admin.positions.policyConsequencesConfirm',
            { terminals: consequences.TerminalIds.length, sessions: consequences.StaffingSessionIds.length },
            `This change affects ${consequences.TerminalIds.length} terminal slots and immediately ends ${consequences.StaffingSessionIds.length} active staffing sessions. Continue?`,
          ))
          if (!confirmed) return
          body.ConfirmTerminalPolicyConsequences = true
        }
      }
      await store.httpClient.addPath(props.id).put(body)
    }
    props.close()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    error.value = err?.data?.Message ?? err?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="briefcase" :footer-button="footerButton">
    <div v-if="!loading || isCreate" class="position-editor">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">
          <span class="tab-label">
            {{ t('admin.positions.tabs.general', {}, 'General') }}
            <CoarPopover v-if="generalIssues.length" mode="hover" :offset="8">
              <span class="tab-issue" role="img" :aria-label="generalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.positions.validation.incomplete', {}, 'Missing information') }}</h4>
                  <ul>
                    <li v-for="issue in generalIssues" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab v-if="!props.draftOnly && !isDraftRow" id="terminals">
          <span class="tab-label">
            {{ t('admin.positions.tabs.terminals', {}, 'Terminals') }}
            <CoarPopover v-if="terminalIssues.length" mode="hover" :offset="8">
              <span class="tab-issue" role="img" :aria-label="terminalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.positions.validation.incomplete', {}, 'Missing information') }}</h4>
                  <ul>
                    <li v-for="issue in terminalIssues" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab v-if="!props.draftOnly && !isDraftRow" id="grants">{{ t('admin.positions.tabs.grants', {}, 'Authorized users') }}</CoarTab>
        <CoarTab v-if="!isCreate && !isDraftRow" id="tokens">{{ t('admin.positions.tabs.tokens', {}, 'Activation tokens') }}</CoarTab>
        <!-- Rule 5: absent in create — sessions cannot exist before the position. -->
        <CoarTab v-if="!isCreate && !isDraftRow" id="sessions">{{ t('admin.positions.tabs.sessions', {}, 'Staffing sessions') }}</CoarTab>
      </CoarTabGroup>

      <CoarNotice v-if="creationCompleted" variant="warning">
        <p class="mb-2 font-medium">{{ t('admin.positionTerminals.secretsOnce', {}, 'Position created. Copy these client secrets now; they will not be shown again.') }}</p>
        <div v-for="secret in createdTerminalSecrets" :key="secret.ClientId" class="mb-2 flex flex-wrap items-center gap-2">
          <code>{{ secret.ClientId }}</code>
          <code class="min-w-0 flex-1 break-all rounded bg-white/40 px-2 py-1">{{ secret.ClientSecret }}</code>
          <CoarButton size="s" variant="secondary" @click="copyText(secret.ClientSecret)">{{ t('common.copy', {}, 'Copy') }}</CoarButton>
        </div>
      </CoarNotice>

      <!-- Tab: General -->
      <div v-show="activeTab === 'general'" class="tab-content modal-form">
        <!-- Section: Basis -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.positions.section.basics', {}, 'Basics') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full" :label="t('admin.positions.accountName', {}, 'Account name')" required
              :error="accountNameError"
              :hint="t('admin.positions.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the token subject handle and audit identity of this position.')">
              <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
                :placeholder="t('admin.positions.accountNamePlaceholder', {}, 'porter.customer-xy, reception.hq, …')" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.positions.purpose', {}, 'Purpose')"
              :hint="t('admin.positions.purposeHint', {}, 'Free text describing what this position is for. Optional.')">
              <CoarTextInput v-model="form.Purpose" clearable
                :placeholder="t('admin.positions.purposePlaceholder', {}, 'Gate porter for customer XY, …')" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Status -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.positions.section.status', {}, 'Status') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.positions.active', {}, 'Active')"
              :hint="t('admin.positions.activeHint', {}, 'Deactivating immediately revokes every outstanding token of this position; staffing and enrollment stay blocked until reactivation.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.IsActive" :disabled="props.draftOnly" />
            </CoarFormField>
          </div>
        </section>
      </div>

      <!-- Tab: Terminals. Rule 1 — the lifetime fields stay VISIBLE when
           terminal use is off (disabled, showing the effective defaults);
           hiding them would make the policy unfindable. -->
      <div v-if="!props.draftOnly && !isDraftRow" v-show="activeTab === 'terminals'" class="tab-content modal-form">
        <section class="form-section">
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.positions.terminalsEnabled', {}, 'Terminal use')"
              :hint="t('admin.positions.terminalsEnabledHint', {}, 'Off by default. Terminal slots can only be created and enrolled while this is on; staff then activate the position with a passkey tap.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.TerminalEnabled" :disabled="props.draftOnly" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.positions.sessionLifetime', {}, 'Staffing session (minutes)')"
              :error="lifetimeError"
              :hint="t('admin.positions.sessionLifetimeHint', {}, 'How long one staffing session lasts — typically a shift (960 = 16 hours). Access tokens stay short-lived independently of this.')">
              <CoarNumberInput v-model="form.StaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.positions.maxSessionLifetime', {}, 'Absolute maximum (minutes)')"
              :hint="t('admin.positions.maxSessionLifetimeHint', {}, 'The hard ceiling a refresh can never extend past (1440 = 24 hours).')">
              <CoarNumberInput v-model="form.MaximumStaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>

            <div class="col-full grid gap-4 md:grid-cols-2">
              <div class="rounded border border-surface-200 p-3">
                <h3 class="mb-1 text-sm font-semibold">
                  {{ t('admin.positions.activationProofs', {}, 'Allowed activation proofs') }}
                </h3>
                <p class="mb-3 text-xs text-surface-500">
                  {{ t('admin.positions.activationProofsHint', {}, 'How a staff member proves they may activate this position.') }}
                </p>
                <div v-for="option in activationProofOptions" :key="option.id" class="mb-2 flex items-start gap-2">
                  <CoarCheckbox
                    :model-value="form.AllowedActivationProofs.includes(option.id)"
                    :disabled="!form.TerminalEnabled || !option.available"
                    @update:model-value="(value) => setAllowed('AllowedActivationProofs', option.id, !!value)" />
                  <div class="min-w-0">
                    <div class="text-sm">{{ option.label }} <code class="text-xs">{{ option.id }}</code></div>
                    <div v-if="!option.available" class="text-xs text-surface-500">
                      {{ t('admin.positions.proofReserved', {}, 'Reserved; not implemented yet.') }}
                    </div>
                  </div>
                </div>
              </div>

              <div class="rounded border border-surface-200 p-3">
                <h3 class="mb-1 text-sm font-semibold">
                  {{ t('admin.positions.deviceBindings', {}, 'Allowed device bindings') }}
                </h3>
                <p class="mb-3 text-xs text-surface-500">
                  {{ t('admin.positions.deviceBindingsHint', {}, 'The immutable binding chosen when a terminal slot is created.') }}
                </p>
                <div v-for="option in deviceBindingOptions" :key="option.id" class="mb-2 flex items-start gap-2">
                  <CoarCheckbox
                    :model-value="form.AllowedDeviceBindings.includes(option.id)"
                    :disabled="!form.TerminalEnabled || !option.available"
                    @update:model-value="(value) => setAllowed('AllowedDeviceBindings', option.id, !!value)" />
                  <div class="min-w-0">
                    <div class="text-sm">{{ option.label }} <code class="text-xs">{{ option.id }}</code></div>
                    <div v-if="!option.available" class="text-xs text-surface-500">
                      {{ t('admin.positions.availableInPhase', { phase: option.phase }, `Available in ${option.phase}.`) }}
                    </div>
                  </div>
                </div>
              </div>
            </div>
            <CoarNotice v-if="realmPositionSecurity" class="col-full" variant="info">
              {{ t('admin.positions.realmFloor', {}, 'Realm floor') }}:
              {{ realmPositionSecurity.RequiredProofCapabilities?.join(', ') || t('common.none', {}, 'none') }} ·
              {{ realmPositionSecurity.RequiredBindingCapabilities?.join(', ') || t('common.none', {}, 'none') }}
            </CoarNotice>
          </div>

          <!-- Terminal slots (MG-FT-03). Rule 1: visible in every state — the
               create row is disabled (with the reason as hint) until the
               PERSISTED policy allows slots. Slot ops are immediate actions. -->
          <div v-if="!props.draftOnly" class="mt-4">
            <div class="mb-3 flex flex-wrap items-end gap-2">
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.name', {}, 'Terminal name')">
                <CoarTextInput v-model="newTerminal.DisplayName" :disabled="!canAddTerminal"
                  :placeholder="t('admin.positionTerminals.namePlaceholder', {}, 'Gate terminal left, …')" />
              </CoarFormField>
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.location', {}, 'Location')">
                <CoarTextInput v-model="newTerminal.Location" :disabled="!canAddTerminal"
                  :placeholder="t('admin.positionTerminals.locationPlaceholder', {}, 'Gate 3, …')" />
              </CoarFormField>
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.rpId', {}, 'WebAuthn RP ID')"
                :hint="existingRpId
                  ? t('admin.positionTerminals.rpIdLockedHint', {}, 'Inherited from the existing slots: staff passkeys hang off the RP ID, so every slot of this position shares it — only then do the already-enrolled tokens unlock a new terminal.')
                  : t('admin.positionTerminals.rpIdHint', {}, 'The RP ID staff passkeys verify against — usually shared by every terminal of the consuming app.')">
                <CoarTextInput v-model="newTerminal.WebAuthnRpId" :disabled="!canAddTerminal || !!existingRpId"
                  placeholder="alerthub.example.com" />
              </CoarFormField>
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.binding', {}, 'Device binding')">
                <CoarSelect v-model="newTerminal.Binding" :options="terminalBindingSelectOptions"
                  :disabled="!canAddTerminal" />
              </CoarFormField>
              <CoarButton size="s" icon-start="plus" class="shrink-0 mb-1"
                :disabled="!canAddTerminal || !newTerminal.DisplayName.trim() || !newTerminal.WebAuthnRpId.trim()"
                @click="isCreate ? stageTerminal() : createTerminal()">
                {{ t('admin.positionTerminals.createButton', {}, 'Add slot') }}
              </CoarButton>
            </div>
            <div v-if="canAddTerminal" class="mb-3 rounded border border-surface-200 p-3">
              <div class="mb-1 text-sm font-semibold">
                {{ t('admin.positionTerminals.allowedPositions', {}, 'Positions available on this terminal') }}
              </div>
              <p class="mb-2 text-xs text-surface-500">
                {{ t('admin.positionTerminals.allowedPositionsHint', {}, 'Select every compatible position before enrollment. Adding another position after enrollment requires a fresh terminal slot and a new Device Flow approval.') }}
              </p>
              <div class="mb-2 flex items-center gap-2 text-sm">
                <CoarCheckbox :model-value="true" disabled />
                <span>{{ isCreate ? (form.AccountName || t('admin.positions.thisPosition', {}, 'This new position')) : positionLabel(props.id) }}</span>
                <CoarTag variant="neutral">{{ t('admin.positions.thisPosition', {}, 'This position') }}</CoarTag>
              </div>
              <div v-for="position in compatibleAdditionalPositions" :key="position.Id"
                  class="mb-2 flex items-center gap-2 text-sm">
                <CoarCheckbox
                  :model-value="newTerminal.AllowedPositionIds.includes(position.Id)"
                  @update:model-value="(value) => setNewTerminalPosition(position.Id, !!value)" />
                <span>{{ position.AccountName }}</span>
              </div>
              <div v-if="compatibleAdditionalPositions.length === 0" class="text-xs text-surface-500">
                {{ t('admin.positionTerminals.noCompatibleAdditionalPositions', {}, 'No other active position currently accepts this device binding.') }}
              </div>
            </div>
            <CoarNotice v-if="!canAddTerminal" variant="info" class="mb-3">
              {{ isCreate
                ? t('admin.positionTerminals.enablePolicyStaged', {}, 'Turn terminal use on to add slots — they are created together with the position.')
                : t('admin.positionTerminals.enablePolicyFirst', {}, 'Enable terminal use and save before creating slots.') }}
            </CoarNotice>
            <CoarNotice v-if="newTerminal.Binding === 'none'" variant="warning" class="mb-3">
              {{ t('admin.positionTerminals.noneWarning', {}, 'No binding means the terminal has no provable device identity. Admin enrollment approval is the only issuance barrier.') }}
            </CoarNotice>
            <CoarNotice v-if="revealedTerminalSecret" variant="warning" class="mb-3">
              <div class="flex flex-wrap items-center gap-2">
                <span>{{ t('admin.positionTerminals.secretOnce', {}, 'Copy this client secret now; it will not be shown again.') }}</span>
                <code class="min-w-0 flex-1 break-all rounded bg-white/40 px-2 py-1">{{ revealedTerminalSecret }}</code>
                <CoarButton size="s" variant="secondary" @click="copyTerminalSecret">
                  {{ t('common.copy', {}, 'Copy') }}
                </CoarButton>
              </div>
            </CoarNotice>

            <!-- Create: the staged slots, committed by the single Save. -->
            <template v-if="isCreate">
              <div v-if="stagedTerminals.length === 0" class="grant-empty">
                {{ t('admin.positionTerminals.emptyStaged', {}, 'No terminal slots staged yet.') }}
              </div>
              <ul v-else class="flex flex-col gap-2">
                <li v-for="(slot, index) in stagedTerminals" :key="`${slot.DisplayName}-${index}`"
                    class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
                  <div class="flex min-w-0 flex-1 flex-col">
                    <span class="truncate font-medium">{{ slot.DisplayName }}</span>
                    <span class="truncate text-xs text-surface-500">
                      {{ slot.WebAuthnRpId }} · {{ slot.Binding }}
                      <template v-if="slot.Location"> · {{ slot.Location }}</template>
                      · {{ slot.AllowedPositionIds.length + 1 }} {{ t('admin.positionTerminals.positions', {}, 'position(s)') }}
                    </span>
                  </div>
                  <CoarTag variant="info">{{ t('admin.positionTerminals.statusStaged', {}, 'On save') }}</CoarTag>
                  <CoarButton size="s" variant="ghost" icon-start="x" @click="unstageTerminal(index)">
                    {{ t('common.remove', {}, 'Remove') }}
                  </CoarButton>
                </li>
              </ul>
            </template>

            <template v-else>
              <div v-if="terminalsLoading" class="text-xs text-surface-500">
                {{ t('common.loading', {}, 'Loading...') }}
              </div>
              <div v-else-if="terminals.length === 0" class="grant-empty">
                {{ t('admin.positionTerminals.empty', {}, 'No terminal slots yet.') }}
              </div>
              <ul v-else class="flex flex-col gap-2">
                <li v-for="terminal in terminals" :key="terminal.Id"
                    class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
                  <div class="flex min-w-0 flex-1 flex-col">
                    <span class="truncate font-medium">{{ terminal.DisplayName }}</span>
                    <span class="truncate text-xs text-surface-500">
                      <code>{{ terminal.ClientId }}</code>
                      <template v-if="terminal.Location"> · {{ terminal.Location }}</template>
                      · {{ terminal.AllowedPositionIds?.length ?? 1 }} {{ t('admin.positionTerminals.positions', {}, 'position(s)') }}
                    </span>
                  </div>
                  <CoarTag :variant="terminal.Status === 'Active' ? 'success'
                    : terminal.Status === 'Pending' ? 'info'
                    : terminal.Status === 'Disabled' ? 'warning' : 'neutral'">
                    {{ terminal.Status === 'Active' ? t('admin.positionTerminals.statusActive', {}, 'Active')
                      : terminal.Status === 'Pending' ? t('admin.positionTerminals.statusPending', {}, 'Pending enrollment')
                      : terminal.Status === 'Disabled' ? t('admin.positionTerminals.statusDisabled', {}, 'Disabled')
                      : t('admin.positionTerminals.statusRevoked', {}, 'Revoked') }}
                  </CoarTag>
                  <CoarTag variant="neutral">{{ terminal.Binding }}</CoarTag>
                  <CoarButton v-if="terminal.Status !== 'Revoked'" size="s" variant="ghost" icon-start="list-checks"
                    @click="editingTerminalPositionsId === terminal.Id
                      ? editingTerminalPositionsId = null
                      : editTerminalPositions(terminal)">
                    {{ t('admin.positionTerminals.editPositions', {}, 'Positions') }}
                  </CoarButton>
                  <div v-if="terminal.Status !== 'Revoked'" class="flex items-center gap-1">
                    <CoarButton v-if="terminal.Status !== 'Disabled'" size="s" variant="ghost" icon-start="pause"
                      @click="transitionTerminal(terminal, 'disable')">
                      {{ t('admin.positionTerminals.disableButton', {}, 'Disable') }}
                    </CoarButton>
                    <CoarButton v-else size="s" variant="ghost" icon-start="play"
                      @click="transitionTerminal(terminal, 'reactivate')">
                      {{ t('admin.positionTerminals.reactivateButton', {}, 'Reactivate') }}
                    </CoarButton>
                    <CoarPopconfirm
                      :title="t('admin.positionTerminals.revokeTitle', {}, 'Revoke terminal?')"
                      :message="t('admin.positionTerminals.revokeConfirm', {}, 'Revoking is permanent: the device is cut off immediately and needs a brand-new slot (with a fresh enrollment) to ever return.')"
                      confirm-variant="danger"
                      @confirmed="transitionTerminal(terminal, 'revoke')">
                      <CoarButton size="s" variant="ghost" icon-start="trash-2">
                        {{ t('admin.positionTerminals.revokeButton', {}, 'Revoke') }}
                      </CoarButton>
                    </CoarPopconfirm>
                  </div>
                  <div v-if="editingTerminalPositionsId === terminal.Id"
                      class="basis-full rounded border border-surface-200 bg-surface-50 p-3">
                    <p v-if="terminal.Enrolled" class="mb-2 text-xs text-surface-500">
                      {{ t('admin.positionTerminals.enrolledAssignmentHint', {}, 'This terminal is already enrolled. Existing positions may be removed, but adding another one requires creating and enrolling a replacement multi-position slot.') }}
                    </p>
                    <div v-for="position in positionsForTerminal(terminal)" :key="position.Id"
                        class="mb-2 flex items-center gap-2 text-sm">
                      <CoarCheckbox
                        :model-value="(terminalPositionDrafts[terminal.Id] ?? terminal.AllowedPositionIds).includes(position.Id)"
                        :disabled="terminal.Enrolled && !terminal.AllowedPositionIds.includes(position.Id)"
                        @update:model-value="(value) => setTerminalPosition(terminal, position.Id, !!value)" />
                      <span>{{ position.AccountName }}</span>
                      <CoarTag v-if="terminal.AllowedPositionIds.includes(position.Id)" variant="neutral">
                        {{ t('admin.positionTerminals.currentAssignment', {}, 'Assigned') }}
                      </CoarTag>
                    </div>
                    <div class="mt-2 flex justify-end gap-2">
                      <CoarButton size="s" variant="ghost" @click="editingTerminalPositionsId = null">
                        {{ t('common.cancel', {}, 'Cancel') }}
                      </CoarButton>
                      <CoarButton size="s"
                        :disabled="(terminalPositionDrafts[terminal.Id] ?? terminal.AllowedPositionIds).length === 0"
                        @click="saveTerminalPositions(terminal)">
                        {{ t('common.save', {}, 'Save') }}
                      </CoarButton>
                    </div>
                  </div>
                </li>
              </ul>
            </template>
          </div>
        </section>
      </div>

      <section v-if="!isCreate && !isDraftRow" v-show="activeTab === 'tokens'" class="form-section tab-content">
        <CoarNotice variant="info">
          {{ t('admin.activationTokens.registrationHint', {}, 'Create and assign the logical token here. Register its WebAuthn credential from an enrolled terminal so registration uses the terminal application’s RP-compatible origin.') }}
        </CoarNotice>
        <div class="flex items-end gap-2">
          <CoarFormField class="min-w-0 flex-1" :label="t('admin.activationTokens.label', {}, 'Token label')">
            <CoarTextInput v-model="newActivationTokenLabel" placeholder="YubiKey safe #1" />
          </CoarFormField>
          <CoarButton size="s" icon-start="plus" :disabled="!newActivationTokenLabel.trim()"
            @click="createActivationToken">
            {{ t('admin.activationTokens.create', {}, 'Create token') }}
          </CoarButton>
        </div>
        <div v-if="activationTokensLoading" class="text-xs text-surface-500">{{ t('common.loading', {}, 'Loading...') }}</div>
        <div v-else-if="activationTokens.length === 0" class="grant-empty">
          {{ t('admin.activationTokens.empty', {}, 'No position-owned activation tokens assigned.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="token in activationTokens" :key="token.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">{{ token.Label }}</span>
              <span class="truncate text-xs text-surface-500">
                {{ token.RegisteredRpIds.length > 0 ? token.RegisteredRpIds.join(', ') : t('admin.activationTokens.notRegistered', {}, 'Not registered on an RP yet') }}
              </span>
            </div>
            <CoarTag :variant="token.Status === 'Active' ? 'success' : token.Status === 'Disabled' ? 'warning' : 'neutral'">
              {{ token.Status }}
            </CoarTag>
            <CoarButton v-if="token.Status === 'Active'" size="s" variant="ghost" icon-start="pause"
              @click="transitionActivationToken(token, 'disable')">{{ t('common.disable', {}, 'Disable') }}</CoarButton>
            <CoarButton v-if="token.Status === 'Disabled'" size="s" variant="ghost" icon-start="play"
              @click="transitionActivationToken(token, 'reactivate')">{{ t('common.reactivate', {}, 'Reactivate') }}</CoarButton>
            <CoarPopconfirm v-if="token.Status !== 'Revoked'"
              :title="t('admin.activationTokens.revokeTitle', {}, 'Revoke activation token?')"
              :message="t('admin.activationTokens.revokeConfirm', {}, 'Revocation is permanent and immediately ends every staffing session activated with this token.')"
              confirm-variant="danger" @confirmed="transitionActivationToken(token, 'revoke')">
              <CoarButton size="s" variant="ghost" icon-start="trash-2">{{ t('common.revoke', {}, 'Revoke') }}</CoarButton>
            </CoarPopconfirm>
          </li>
        </ul>
      </section>

      <!-- Staffing sessions (MG-FT-05/07) — the live/audit view of shifts on
           this position's terminals; force-lock ends a running one remotely.
           Edit-mode only: sessions cannot exist before the position does. -->
      <section v-if="!isCreate && !isDraftRow" v-show="activeTab === 'sessions'" class="form-section tab-content">
        <div v-if="sessionsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="staffingSessions.length === 0" class="grant-empty">
          {{ t('admin.staffingSessions.empty', {}, 'No staffing sessions yet.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="s in staffingSessions" :key="s.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">
                {{ terminalLabel(s.TerminalId) }}
                <span class="font-normal text-surface-500">
                  · {{ s.ActivationProof === 'position-token'
                    ? `${t('admin.activationTokens.token', {}, 'Token')} ${s.ActivationTokenId ?? ''}`
                    : s.ActivatedByUserId ? userLabel(s.ActivatedByUserId) : s.ActivationProof }}
                </span>
              </span>
              <span class="truncate text-xs text-surface-500">
                {{ formatTime(s.StartedAt) }}
                <template v-if="s.Status === 'Active'">
                  → {{ t('admin.staffingSessions.until', {}, 'until at most') }} {{ formatTime(s.AbsoluteExpiresAt) }}
                </template>
                <template v-else>
                  → {{ formatTime(s.EndedAt) }} · {{ endReasonLabel(s.EndReason) }}
                </template>
              </span>
            </div>
            <CoarTag :variant="s.Status === 'Active' ? 'success' : 'neutral'">
              {{ s.Status === 'Active'
                ? t('admin.staffingSessions.statusActive', {}, 'Active')
                : t('admin.staffingSessions.statusEnded', {}, 'Ended') }}
            </CoarTag>
            <CoarPopconfirm v-if="s.Status === 'Active'"
              :title="t('admin.staffingSessions.forceLockTitle', {}, 'Force-lock session?')"
              :message="t('admin.staffingSessions.forceLockConfirm', {}, 'The terminal is locked immediately and its tokens are revoked; staff must tap their passkey again to continue.')"
              confirm-variant="danger"
              @confirmed="forceLockSession(s.Id)">
              <CoarButton size="s" variant="ghost" icon-start="lock">
                {{ t('admin.staffingSessions.forceLockButton', {}, 'Force-lock') }}
              </CoarButton>
            </CoarPopconfirm>
          </li>
        </ul>
      </section>

      <!-- Rule 5: same section in both modes — create STAGES grants (the one
           Save commits position + grants atomically), edit operates on live
           grants immediately (rule 2: own lifecycle, explicit actions). -->
      <section v-if="!isDraftRow" v-show="activeTab === 'grants'" class="form-section tab-content">
        <div class="mb-3 flex items-center gap-2">
          <CoarSelect
            v-model="selectedGrantUserId"
            :options="grantableUserOptions"
            searchable
            class="min-w-0 flex-1"
            :placeholder="t('admin.positionGrants.pickUser', {}, 'Select a user…')" />
          <CoarButton size="s" icon-start="plus" class="shrink-0" :disabled="!selectedGrantUserId"
            @click="isCreate ? stageGrant() : issueGrant()">
            {{ t('admin.positionGrants.issueButton', {}, 'Grant') }}
          </CoarButton>
        </div>

        <template v-if="isCreate">
          <div v-if="stagedGrantUserIds.length === 0" class="grant-empty">
            {{ t('admin.positionGrants.stagedEmpty', {}, 'No users staged yet — they are authorized together with the create.') }}
          </div>
          <ul v-else class="flex flex-col gap-2">
            <li v-for="userId in stagedGrantUserIds" :key="userId"
                class="flex items-center gap-2 rounded border border-surface-200 p-3">
              <span class="min-w-0 flex-1 truncate font-medium">{{ userLabel(userId) }}</span>
              <CoarButton size="s" variant="ghost" icon-start="trash-2" @click="unstageGrant(userId)">
                {{ t('common.remove', {}, 'Remove') }}
              </CoarButton>
            </li>
          </ul>
        </template>

        <div v-else-if="grantsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="grants.length === 0" class="grant-empty">
          {{ t('admin.positionGrants.empty', {}, 'No user is authorized to staff this position yet.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="grant in grants" :key="grant.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">{{ grant.UserDisplayName || grant.UserAccountName || grant.UserId }}</span>
              <span v-if="grant.UserAccountName" class="truncate text-xs text-surface-500">{{ grant.UserAccountName }}</span>
            </div>
            <CoarTag v-if="!grant.UserHasPasskey && grant.Status !== 'Revoked'" variant="warning">
              {{ t('admin.positionGrants.noPasskey', {}, 'No passkey') }}
            </CoarTag>
            <CoarTag :variant="grant.Status === 'Active' ? 'success' : grant.Status === 'Suspended' ? 'warning' : 'neutral'">
              {{ grant.Status === 'Active'
                ? t('admin.positionGrants.statusActive', {}, 'Active')
                : grant.Status === 'Suspended'
                  ? t('admin.positionGrants.statusSuspended', {}, 'Suspended')
                  : t('admin.positionGrants.statusRevoked', {}, 'Revoked') }}
            </CoarTag>
            <div v-if="grant.Status !== 'Revoked'" class="flex items-center gap-1">
              <CoarButton v-if="grant.Status === 'Active'" size="s" variant="ghost" icon-start="pause"
                @click="transitionGrant(grant, 'suspend')">
                {{ t('admin.positionGrants.suspendButton', {}, 'Suspend') }}
              </CoarButton>
              <CoarButton v-else size="s" variant="ghost" icon-start="play"
                @click="transitionGrant(grant, 'resume')">
                {{ t('admin.positionGrants.resumeButton', {}, 'Resume') }}
              </CoarButton>
              <CoarPopconfirm
                :title="t('admin.positionGrants.revokeTitle', {}, 'Revoke grant?')"
                :message="t('admin.positionGrants.revokeConfirm', {}, 'Revoking is permanent — re-authorizing this user later creates a new grant with a fresh audit trail.')"
                confirm-variant="danger"
                @confirmed="transitionGrant(grant, 'revoke')">
                <CoarButton size="s" variant="ghost" icon-start="trash-2">
                  {{ t('admin.positionGrants.revokeButton', {}, 'Revoke') }}
                </CoarButton>
              </CoarPopconfirm>
            </div>
          </li>
        </ul>
      </section>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
/* Pinned body height so the modal keeps ONE size across all tabs (no resize
   on tab switch) — same pattern as .user-edit-frame in UserDetails. Applies
   in create too, because create is tabbed as well. flex: 0 0 auto is required
   so the height wins inside the modal's flex column. */
.position-editor {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
  min-width: 0;
  min-height: 0;
  padding: 0.25rem;
  flex: 0 0 auto;
  height: 60vh;
}

.form-section + .form-section {
  margin-top: 1.5rem;
}

.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.grant-empty {
  padding: 1rem;
  border: 1px dashed var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: 0.25rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.875rem;
  text-align: center;
}

.tab-bar {
  margin-bottom: 12px;
}

.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 2px 2px 16px;
  min-height: 0;
  /* Long grant/slot/session lists scroll inside the pinned frame instead of
     growing the modal. */
  overflow-y: auto;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.tab-issue {
  display: flex;
  align-items: center;
  color: var(--coar-text-warning-primary, #b45309);
  cursor: help;
}

.tab-issue-panel {
  width: min(24rem, 70vw);
  padding: 0.75rem 0.875rem;
}

.tab-issue-panel h4 {
  margin: 0 0 0.4rem;
  font-size: 0.875rem;
  font-weight: 600;
}

.tab-issue-panel ul {
  margin: 0;
  padding-left: 1rem;
  font-size: 0.8rem;
}
</style>
