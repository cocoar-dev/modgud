<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { CoarIcon, CoarButton, CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'

const { t } = useI18n()

defineProps<{
  // Required by routedFragments path schema but unused — the modal has no
  // per-id surface, it always shows the latest run. Optional on the type
  // to silence "missing required prop" when the host doesn't pass one.
  id?: string
  close: (result?: unknown) => void
}>()

const projectionHttp = useHttpClient('/api/admin/projections')

// Response shape is contractually mirrored from
// `Modgud.Api/Features/Admin/ProjectionEndpoints.cs::CheckResult`.
// All issue collections are optional — never throw on a missing key, the
// previous regression on a bare `.length` cost us a blank page (commit
// 817f236 carries the lesson + fixed schema).
interface IdName { Id: string; Label: string }
interface DanglingMember { GroupId: string; GroupName: string; Member: IdName }
interface DanglingRole { GroupId: string; GroupName: string; RoleId: string }
interface CycleReport { Groups: IdName[] }
interface AutoDriftIssue {
  GroupId: string
  GroupName: string
  ScriptError: boolean
  MissingMembers: IdName[]
  ExtraMembers: IdName[]
}

interface CheckBlock {
  Id: string
  Title: string
  Description: string
  Status: 'OK' | 'ISSUES_FOUND'
  // Sub-millisecond precision — checks on dev realms typically clock at
  // 0.1–2 ms, displaying a `long` rounded-to-int was reading as "0 ms"
  // for every check and looked like the button did nothing.
  DurationMs: number
  Summary: string
  // Per-check shape — anything we read is guarded with ?. fallback below.
  Issues: {
    MissingPerson?: IdName[]
    OrphanPerson?: IdName[]
    MissingGroup?: IdName[]
    OrphanGroup?: IdName[]
    Items?: (DanglingMember | DanglingRole | AutoDriftIssue)[]
    Cycles?: CycleReport[]
  }
}

interface Report {
  Status: 'OK' | 'ISSUES_FOUND'
  RunAt: string
  DurationTotalMs: number
  Totals: {
    ApplicationUsers: number
    AuthorizationGroups: number
    PrincipalsTotal: number
    PrincipalsPerson: number
    PrincipalsGroup: number
    Roles: number
  }
  Checks: CheckBlock[]
}

const loading = ref(false)
const report = ref<Report | null>(null)
const error = ref<string | null>(null)
// Per-check expand state. ISSUES_FOUND auto-expand; OK collapse.
const expanded = ref<Record<string, boolean>>({})

async function runCheck() {
  loading.value = true
  error.value = null
  report.value = null
  try {
    report.value = await projectionHttp.addPath('consistency-check').get<Report>()
    // Auto-expand any check with issues so the admin sees them without
    // having to click; OK checks stay collapsed so the wall-of-green
    // stays scannable.
    expanded.value = Object.fromEntries(
      (report.value?.Checks ?? []).map((c) => [c.Id, c.Status === 'ISSUES_FOUND']),
    )
  } catch (e: any) {
    error.value = e?.data?.Message
      ?? e?.message
      ?? t('admin.consistency.runFailed', {}, 'Consistency check failed')
  } finally {
    loading.value = false
  }
}

function toggle(checkId: string) {
  expanded.value[checkId] = !expanded.value[checkId]
}

const runAtLocal = computed(() => {
  if (!report.value?.RunAt) return ''
  return new Date(report.value.RunAt).toLocaleString()
})

// Pretty-print timing. Backend rounds to 2 decimals via
// Math.Round(elapsed.TotalMilliseconds, 2). Display rules:
//   < 1 ms      → "< 1 ms" (sub-ms is noise to humans — the precision
//                 is honest, but reading 0.04 / 0.07 / 0.12 ms per
//                 check is exhaust, not signal)
//   < 10 ms     → 1 decimal: "1.7 ms"
//   ≥ 10 ms    → whole number: "24 ms"
//
// The total at the top of the report uses the same formatter, so a
// 0.4 ms total still reads as "< 1 ms" — consistent with the per-check
// rule. If the total is genuinely sub-ms the work was indeed
// near-zero (tiny dev realm), and "< 1 ms" beats "0.4 ms" for
// scannability.
function formatMs(ms: number): string {
  if (!Number.isFinite(ms)) return '—'
  if (ms < 1) return '< 1 ms'
  if (ms < 10) return `${ms.toFixed(1)} ms`
  return `${Math.round(ms)} ms`
}

// ─── Narrow-type helpers for the template ──────────────────────────
// Each check carries a unique `Issues` shape; cast-narrowing keeps the
// template tight without `as` clutter inline.
function asPrincipalSync(c: CheckBlock) {
  return c.Issues as {
    MissingPerson?: IdName[]; OrphanPerson?: IdName[]
    MissingGroup?: IdName[]; OrphanGroup?: IdName[]
  }
}
function asDanglingMembers(c: CheckBlock) {
  return (c.Issues.Items as DanglingMember[]) ?? []
}
function asDanglingRoles(c: CheckBlock) {
  return (c.Issues.Items as DanglingRole[]) ?? []
}
function asCycles(c: CheckBlock) {
  return c.Issues.Cycles ?? []
}
function asAutoDrift(c: CheckBlock) {
  return (c.Issues.Items as AutoDriftIssue[]) ?? []
}

// Backend ships check titles in English (its convention — no locale
// negotiation anywhere). For the well-known check ids we look up a
// FE-side translation, falling back to the backend's title when an
// id we don't know shows up (forward-compat). Descriptions + summaries
// stay backend-supplied — long, technical, data-driven; admins doing
// maintenance read English fine.
function localizedTitle(check: CheckBlock): string {
  return t(`admin.consistency.checks.${check.Id}.title`, {}, check.Title)
}

onMounted(runCheck)
</script>

<template>
  <ModalLayout
    :close="close"
    :title="t('admin.consistency.title', {}, 'Consistency check')"
    :sub-title="report ? runAtLocal : undefined"
    icon="shield-check"
  >
    <div class="flex flex-col gap-4 p-2 sm:p-4">
      <!-- Loading state -->
      <div v-if="loading" class="flex items-center gap-3 rounded-lg border border-surface-200 bg-surface-50 p-4">
        <CoarIcon name="loader" size="m" class="animate-spin text-surface-500" />
        <span class="text-sm text-surface-600">
          {{ t('admin.consistency.running', {}, 'Running checks…') }}
        </span>
      </div>

      <!-- Error state -->
      <div v-else-if="error" class="rounded-lg border border-red-300 bg-red-50 p-4">
        <div class="flex items-center gap-2 text-red-800 font-semibold mb-1">
          <CoarIcon name="alert-triangle" size="m" class="text-red-600" />
          {{ t('admin.consistency.errorTitle', {}, 'Check could not run') }}
        </div>
        <div class="text-sm text-red-700">{{ error }}</div>
        <CoarButton variant="secondary" size="s" class="mt-3" @click="runCheck">
          {{ t('admin.consistency.retry', {}, 'Retry') }}
        </CoarButton>
      </div>

      <!-- Report -->
      <template v-else-if="report">
        <!-- Overall status banner -->
        <div
          class="rounded-lg border p-4 flex items-start gap-3"
          :class="report.Status === 'OK'
            ? 'border-green-300 bg-green-50'
            : 'border-amber-300 bg-amber-50'"
        >
          <CoarIcon
            :name="report.Status === 'OK' ? 'check-circle' : 'alert-triangle'"
            size="l"
            :class="report.Status === 'OK' ? 'text-green-600' : 'text-amber-600'"
          />
          <div class="flex-1 min-w-0">
            <div
              class="font-semibold"
              :class="report.Status === 'OK' ? 'text-green-800' : 'text-amber-800'"
            >
              {{ report.Status === 'OK'
                ? t('admin.consistency.statusOk', {}, 'All consistent')
                : t('admin.consistency.statusIssues', {}, 'Issues found') }}
            </div>
            <div class="text-xs text-surface-600 mt-1">
              {{ report.Checks.length }} {{ t('admin.consistency.checksRun', {}, 'checks run') }}
              · {{ formatMs(report.DurationTotalMs) }}
              · {{ runAtLocal }}
            </div>
          </div>
        </div>

        <!-- Totals -->
        <div class="rounded-lg border border-surface-200 bg-surface-50 p-3">
          <div class="text-xs font-medium text-surface-500 mb-2">
            {{ t('admin.consistency.dataBaseline', {}, 'Data baseline') }}
          </div>
          <div class="flex flex-wrap gap-x-6 gap-y-1 text-sm">
            <span><strong>{{ report.Totals.ApplicationUsers }}</strong>
              {{ t('admin.consistency.totalsUsers', {}, 'users') }}</span>
            <span><strong>{{ report.Totals.AuthorizationGroups }}</strong>
              {{ t('admin.consistency.totalsGroups', {}, 'groups') }}</span>
            <span><strong>{{ report.Totals.PrincipalsTotal }}</strong>
              {{ t('admin.consistency.totalsPrincipals', {}, 'principals') }}
              <span class="text-surface-500">
                ({{ report.Totals.PrincipalsPerson }} person
                / {{ report.Totals.PrincipalsGroup }} group)
              </span>
            </span>
            <span><strong>{{ report.Totals.Roles }}</strong>
              {{ t('admin.consistency.totalsRoles', {}, 'roles') }}</span>
          </div>
        </div>

        <!-- Per-check cards -->
        <div class="space-y-3">
          <div
            v-for="check in report.Checks"
            :key="check.Id"
            class="rounded-lg border overflow-hidden"
            :class="check.Status === 'OK'
              ? 'border-green-200 bg-white'
              : 'border-amber-300 bg-white'"
          >
            <!-- Card header — always visible, click to toggle -->
            <button
              type="button"
              class="w-full flex items-start gap-3 p-3 text-left hover:bg-surface-50 transition-colors"
              @click="toggle(check.Id)"
            >
              <CoarIcon
                :name="check.Status === 'OK' ? 'check-circle' : 'alert-triangle'"
                size="m"
                :class="check.Status === 'OK' ? 'text-green-600' : 'text-amber-600'"
                class="shrink-0 mt-0.5"
              />
              <div class="flex-1 min-w-0">
                <div class="flex items-baseline gap-2 flex-wrap">
                  <span class="font-semibold text-surface-900">{{ localizedTitle(check) }}</span>
                  <CoarTag size="s" :variant="check.Status === 'OK' ? 'success' : 'warning'">
                    {{ check.Status === 'OK'
                      ? t('admin.consistency.tagOk', {}, 'OK')
                      : t('admin.consistency.tagIssues', {}, 'Issues') }}
                  </CoarTag>
                  <span class="text-xs text-surface-500 ml-auto">{{ formatMs(check.DurationMs) }}</span>
                </div>
                <div class="text-sm text-surface-700 mt-0.5">{{ check.Summary }}</div>
              </div>
              <CoarIcon
                :name="expanded[check.Id] ? 'chevron-up' : 'chevron-down'"
                size="s"
                class="shrink-0 mt-1 text-surface-500"
              />
            </button>

            <!-- Card body — description + details -->
            <div v-if="expanded[check.Id]" class="border-t border-surface-200 p-3 space-y-3 bg-surface-50">
              <p class="text-xs text-surface-600 leading-relaxed">{{ check.Description }}</p>

              <!-- Principal-sync block -->
              <template v-if="check.Id === 'principal-sync'">
                <div v-if="check.Status === 'OK'" class="text-sm text-green-700">
                  <CoarIcon name="check" size="xs" class="inline" />
                  {{ t('admin.consistency.principalSyncOk', {},
                    'Every source document has a matching principal entry. Nothing to clean up.') }}
                </div>
                <div v-else class="space-y-2">
                  <div v-if="asPrincipalSync(check).MissingPerson?.length"
                    class="rounded border border-amber-200 bg-white p-2">
                    <div class="text-xs font-medium text-amber-800 mb-1">
                      {{ t('admin.consistency.principalSync.missingPerson', {},
                        'ApplicationUsers without a Person principal') }}
                      ({{ asPrincipalSync(check).MissingPerson!.length }})
                    </div>
                    <ul class="text-xs space-y-0.5">
                      <li v-for="m in asPrincipalSync(check).MissingPerson" :key="m.Id"
                        class="font-mono">{{ m.Label }}</li>
                    </ul>
                  </div>
                  <div v-if="asPrincipalSync(check).OrphanPerson?.length"
                    class="rounded border border-amber-200 bg-white p-2">
                    <div class="text-xs font-medium text-amber-800 mb-1">
                      {{ t('admin.consistency.principalSync.orphanPerson', {},
                        'Person principals with no ApplicationUser source') }}
                      ({{ asPrincipalSync(check).OrphanPerson!.length }})
                    </div>
                    <ul class="text-xs space-y-0.5">
                      <li v-for="o in asPrincipalSync(check).OrphanPerson" :key="o.Id"
                        class="font-mono">{{ o.Label }}</li>
                    </ul>
                  </div>
                  <div v-if="asPrincipalSync(check).MissingGroup?.length"
                    class="rounded border border-amber-200 bg-white p-2">
                    <div class="text-xs font-medium text-amber-800 mb-1">
                      {{ t('admin.consistency.principalSync.missingGroup', {},
                        'Groups without a Group principal') }}
                      ({{ asPrincipalSync(check).MissingGroup!.length }})
                    </div>
                    <ul class="text-xs space-y-0.5">
                      <li v-for="m in asPrincipalSync(check).MissingGroup" :key="m.Id"
                        class="font-mono">{{ m.Label }}</li>
                    </ul>
                  </div>
                  <div v-if="asPrincipalSync(check).OrphanGroup?.length"
                    class="rounded border border-amber-200 bg-white p-2">
                    <div class="text-xs font-medium text-amber-800 mb-1">
                      {{ t('admin.consistency.principalSync.orphanGroup', {},
                        'Group principals with no Group source') }}
                      ({{ asPrincipalSync(check).OrphanGroup!.length }})
                    </div>
                    <ul class="text-xs space-y-0.5">
                      <li v-for="o in asPrincipalSync(check).OrphanGroup" :key="o.Id"
                        class="font-mono">{{ o.Label }}</li>
                    </ul>
                  </div>
                </div>
              </template>

              <!-- Dangling members -->
              <template v-else-if="check.Id === 'dangling-members'">
                <div v-if="check.Status === 'OK'" class="text-sm text-green-700">
                  <CoarIcon name="check" size="xs" class="inline" />
                  {{ t('admin.consistency.danglingMembersOk', {},
                    'All MemberIds across every group resolve to a live principal.') }}
                </div>
                <ul v-else class="rounded border border-amber-200 bg-white p-2 space-y-1 text-xs">
                  <li v-for="(item, i) in asDanglingMembers(check)" :key="i">
                    <strong>{{ item.GroupName }}</strong>
                    <span class="text-surface-400"> → </span>
                    <span class="font-mono">{{ item.Member.Label }}</span>
                  </li>
                </ul>
              </template>

              <!-- Dangling roles -->
              <template v-else-if="check.Id === 'dangling-roles'">
                <div v-if="check.Status === 'OK'" class="text-sm text-green-700">
                  <CoarIcon name="check" size="xs" class="inline" />
                  {{ t('admin.consistency.danglingRolesOk', {},
                    'All RoleIds across every group resolve to a live role.') }}
                </div>
                <ul v-else class="rounded border border-amber-200 bg-white p-2 space-y-1 text-xs">
                  <li v-for="(item, i) in asDanglingRoles(check)" :key="i">
                    <strong>{{ item.GroupName }}</strong>
                    <span class="text-surface-400"> → </span>
                    <span class="font-mono">{{ item.RoleId }}</span>
                  </li>
                </ul>
              </template>

              <!-- Group cycles -->
              <template v-else-if="check.Id === 'group-cycles'">
                <div v-if="check.Status === 'OK'" class="text-sm text-green-700">
                  <CoarIcon name="check" size="xs" class="inline" />
                  {{ t('admin.consistency.cyclesOk', {},
                    'No cycles detected in the group-member graph.') }}
                </div>
                <div v-else class="space-y-2">
                  <div v-for="(c, i) in asCycles(check)" :key="i"
                    class="rounded border border-amber-200 bg-white p-2 text-xs">
                    {{ c.Groups.map((g: IdName) => g.Label).join(' → ') }} → {{ c.Groups[0]?.Label }}
                  </div>
                </div>
              </template>

              <!-- Auto-group drift -->
              <template v-else-if="check.Id === 'auto-group-drift'">
                <div v-if="check.Status === 'OK'" class="text-sm text-green-700">
                  <CoarIcon name="check" size="xs" class="inline" />
                  {{ t('admin.consistency.autoDriftOk', {},
                    'Every auto-group matches its predicate.') }}
                </div>
                <div v-else class="space-y-2">
                  <div v-for="d in asAutoDrift(check)" :key="d.GroupId"
                    class="rounded border border-amber-200 bg-white p-2 text-xs space-y-1">
                    <div class="font-medium">{{ d.GroupName }}</div>
                    <div v-if="d.ScriptError" class="text-red-700">
                      {{ t('admin.consistency.autoDrift.scriptError', {},
                        'Predicate did not compile — fix the group\'s membership script.') }}
                    </div>
                    <div v-if="d.MissingMembers.length > 0">
                      <strong>{{ t('admin.consistency.autoDrift.shouldBe', {}, 'Should be members') }}
                        ({{ d.MissingMembers.length }}):</strong>
                      {{ d.MissingMembers.map((m: IdName) => m.Label).join(', ') }}
                    </div>
                    <div v-if="d.ExtraMembers.length > 0">
                      <strong>{{ t('admin.consistency.autoDrift.shouldNotBe', {}, 'Should not be members') }}
                        ({{ d.ExtraMembers.length }}):</strong>
                      {{ d.ExtraMembers.map((m: IdName) => m.Label).join(', ') }}
                    </div>
                  </div>
                </div>
              </template>
            </div>
          </div>
        </div>

        <!-- Re-run -->
        <div class="flex justify-end pt-2">
          <CoarButton variant="secondary" size="s" :loading="loading" @click="runCheck">
            {{ t('admin.consistency.rerun', {}, 'Re-run check') }}
          </CoarButton>
        </div>
      </template>
    </div>
  </ModalLayout>
</template>

<style scoped>
.animate-spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
