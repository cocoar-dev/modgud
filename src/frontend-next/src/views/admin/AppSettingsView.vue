<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon } from '@cocoar/vue-ui'

const { t, language } = useI18n()
const projectionHttp = useHttpClient('/api/admin/projections')

const ui = useUI()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('nav.settings', {}, 'Settings')
  ctx.header.icon = 'settings'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

// Projection rebuild
const rebuilding = ref(false)
const rebuildResult = ref<{ ok: boolean; message: string } | null>(null)

async function rebuildProjections() {
  rebuilding.value = true
  rebuildResult.value = null
  try {
    await projectionHttp.addPath('rebuild').post()
    rebuildResult.value = { ok: true, message: t('admin.settings.rebuildSuccess', {}, 'Projections rebuilt successfully.') }
  } catch (e: any) {
    rebuildResult.value = { ok: false, message: e?.data?.Message || t('admin.settings.rebuildFailed', {}, 'Rebuild failed.') }
  } finally {
    rebuilding.value = false
    setTimeout(() => rebuildResult.value = null, 5000)
  }
}

// Consistency check — shape matches the new backend endpoint for the IPrincipal model.
interface GroupRef { Id: string; Name: string }
interface AutoGroupDrift {
  GroupId: string
  GroupName: string
  ScriptError: boolean
  MissingMembers: string[]
  ExtraMembers: string[]
}
interface DanglingRef { GroupId?: string; GroupName?: string; MemberId?: string; RoleId?: string; TodoId?: string; TodoTitle?: string; PrincipalId?: string; Label?: string | null }

interface ConsistencyReport {
  Status: 'OK' | 'ISSUES_FOUND'
  Totals: {
    ApplicationUsers: number
    AuthorizationGroups: number
    PrincipalsTotal: number
    PrincipalsPerson: number
    PrincipalsGroup: number
    Roles: number
    Todos: number
  }
  PrincipalValidation: {
    MissingPerson: string[]
    OrphanPerson: string[]
    MissingGroup: string[]
    OrphanGroup: string[]
  }
  DanglingReferences: {
    MembersInGroups: DanglingRef[]
    RolesInGroups: DanglingRef[]
    ResponsiblesInTodos: DanglingRef[]
    CreatorsInTodos: DanglingRef[]
  }
  GroupCycles: { Groups: GroupRef[] }[]
  AutoGroupDrift: AutoGroupDrift[]
}

const checking = ref(false)
const checkReport = ref<ConsistencyReport | null>(null)

async function runConsistencyCheck() {
  checking.value = true
  checkReport.value = null
  try {
    checkReport.value = await projectionHttp.addPath('consistency-check').get<ConsistencyReport>()
  } catch (e: any) {
    console.error('Consistency check failed', e)
  } finally {
    checking.value = false
  }
}

const principalIssueCount = computed(() => {
  const pv = checkReport.value?.PrincipalValidation
  if (!pv) return 0
  return pv.MissingPerson.length + pv.OrphanPerson.length
       + pv.MissingGroup.length + pv.OrphanGroup.length
})

const danglingIssueCount = computed(() => {
  const d = checkReport.value?.DanglingReferences
  if (!d) return 0
  return d.MembersInGroups.length + d.RolesInGroups.length
       + d.ResponsiblesInTodos.length + d.CreatorsInTodos.length
})
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 overflow-auto p-4">
    <div class="w-full mx-auto space-y-6">
      <CoarCard elevated>
        <div class="p-6 space-y-4">
          <h2 class="text-lg font-semibold">{{ t('admin.settings.maintenance', {}, 'Maintenance') }}</h2>

          <!-- Consistency Check -->
          <div class="space-y-3">
            <div class="flex items-center gap-4">
              <CoarButton variant="secondary" size="s" :loading="checking" @click="runConsistencyCheck">
                {{ t('admin.settings.consistencyCheck', {}, 'Consistency Check') }}
              </CoarButton>
              <p class="text-sm text-surface-500">
                {{ t('admin.settings.consistencyDescription', {},
                  'Verifies that principal validation, group memberships, auto-group rules, and cross-references are in sync.') }}
              </p>
            </div>

            <!-- Report -->
            <div v-if="checkReport" class="rounded-lg border p-4 space-y-3"
              :class="checkReport.Status === 'OK' ? 'border-green-300 bg-green-50' : 'border-red-300 bg-red-50'">
              <div class="flex items-center gap-2">
                <CoarIcon :name="checkReport.Status === 'OK' ? 'check-circle' : 'alert-triangle'"
                  :class="checkReport.Status === 'OK' ? 'text-green-600' : 'text-red-600'" size="m" />
                <span class="font-semibold" :class="checkReport.Status === 'OK' ? 'text-green-800' : 'text-red-800'">
                  {{ checkReport.Status === 'OK'
                    ? t('admin.settings.consistencyOk', {}, 'All consistent')
                    : t('admin.settings.consistencyFailed', {}, 'Issues found') }}
                </span>
              </div>

              <div class="text-sm text-surface-600 flex flex-wrap gap-x-6 gap-y-1">
                <span>Users: {{ checkReport.Totals.ApplicationUsers }}</span>
                <span>Groups: {{ checkReport.Totals.AuthorizationGroups }}</span>
                <span>Principals: {{ checkReport.Totals.PrincipalsTotal }}
                  ({{ checkReport.Totals.PrincipalsPerson }} person / {{ checkReport.Totals.PrincipalsGroup }} group)</span>
                <span>Roles: {{ checkReport.Totals.Roles }}</span>
                <span>Todos: {{ checkReport.Totals.Todos }}</span>
              </div>

              <!-- PrincipalValidation drift -->
              <div v-if="principalIssueCount > 0" class="space-y-2">
                <p class="text-sm font-medium text-red-800">
                  {{ t('admin.settings.consistencyPrincipalDrift', {}, 'PrincipalValidation drift') }}:
                </p>
                <div class="rounded border border-red-200 bg-white p-3 text-xs space-y-1">
                  <div v-if="checkReport.PrincipalValidation.MissingPerson.length > 0">
                    <strong>Missing Person entries</strong> ({{ checkReport.PrincipalValidation.MissingPerson.length }}):
                    {{ checkReport.PrincipalValidation.MissingPerson.join(', ') }}
                  </div>
                  <div v-if="checkReport.PrincipalValidation.OrphanPerson.length > 0">
                    <strong>Orphan Person entries</strong> ({{ checkReport.PrincipalValidation.OrphanPerson.length }}):
                    {{ checkReport.PrincipalValidation.OrphanPerson.join(', ') }}
                  </div>
                  <div v-if="checkReport.PrincipalValidation.MissingGroup.length > 0">
                    <strong>Missing Group entries</strong> ({{ checkReport.PrincipalValidation.MissingGroup.length }}):
                    {{ checkReport.PrincipalValidation.MissingGroup.join(', ') }}
                  </div>
                  <div v-if="checkReport.PrincipalValidation.OrphanGroup.length > 0">
                    <strong>Orphan Group entries</strong> ({{ checkReport.PrincipalValidation.OrphanGroup.length }}):
                    {{ checkReport.PrincipalValidation.OrphanGroup.join(', ') }}
                  </div>
                </div>
              </div>

              <!-- Dangling references -->
              <div v-if="danglingIssueCount > 0" class="space-y-2">
                <p class="text-sm font-medium text-red-800">
                  {{ t('admin.settings.consistencyDanglingRefs', {}, 'Dangling references') }}:
                </p>
                <div class="rounded border border-red-200 bg-white p-3 text-xs space-y-2">
                  <div v-if="checkReport.DanglingReferences.MembersInGroups.length > 0">
                    <strong>Members in groups</strong> ({{ checkReport.DanglingReferences.MembersInGroups.length }}):
                    <ul class="ml-4 list-disc">
                      <li v-for="(m, i) in checkReport.DanglingReferences.MembersInGroups" :key="i">
                        {{ m.GroupName }} → {{ m.MemberId }}
                      </li>
                    </ul>
                  </div>
                  <div v-if="checkReport.DanglingReferences.RolesInGroups.length > 0">
                    <strong>Roles in groups</strong> ({{ checkReport.DanglingReferences.RolesInGroups.length }}):
                    <ul class="ml-4 list-disc">
                      <li v-for="(r, i) in checkReport.DanglingReferences.RolesInGroups" :key="i">
                        {{ r.GroupName }} → {{ r.RoleId }}
                      </li>
                    </ul>
                  </div>
                  <div v-if="checkReport.DanglingReferences.ResponsiblesInTodos.length > 0">
                    <strong>Responsibles in todos</strong> ({{ checkReport.DanglingReferences.ResponsiblesInTodos.length }}):
                    <ul class="ml-4 list-disc">
                      <li v-for="(t, i) in checkReport.DanglingReferences.ResponsiblesInTodos" :key="i">
                        {{ t.TodoTitle }} → {{ t.Label || t.PrincipalId }}
                      </li>
                    </ul>
                  </div>
                  <div v-if="checkReport.DanglingReferences.CreatorsInTodos.length > 0">
                    <strong>Creators in todos</strong> ({{ checkReport.DanglingReferences.CreatorsInTodos.length }}):
                    <ul class="ml-4 list-disc">
                      <li v-for="(t, i) in checkReport.DanglingReferences.CreatorsInTodos" :key="i">
                        {{ t.TodoTitle }} → {{ t.PrincipalId }}
                      </li>
                    </ul>
                  </div>
                </div>
              </div>

              <!-- Cycles -->
              <div v-if="checkReport.GroupCycles.length > 0" class="space-y-2">
                <p class="text-sm font-medium text-red-800">
                  {{ t('admin.settings.consistencyCycles', {}, 'Group cycles') }}:
                </p>
                <div v-for="(c, i) in checkReport.GroupCycles" :key="i"
                  class="rounded border border-red-200 bg-white p-3 text-xs">
                  {{ c.Groups.map(g => g.Name).join(' → ') }}
                </div>
              </div>

              <!-- Auto-group drift -->
              <div v-if="checkReport.AutoGroupDrift.length > 0" class="space-y-2">
                <p class="text-sm font-medium text-red-800">
                  {{ t('admin.settings.consistencyAutoDrift', {}, 'Auto-group membership drift') }}:
                </p>
                <div v-for="d in checkReport.AutoGroupDrift" :key="d.GroupId"
                  class="rounded border border-red-200 bg-white p-3 text-xs space-y-1">
                  <div class="font-medium">{{ d.GroupName }}</div>
                  <div v-if="d.ScriptError" class="text-red-700">Script error — check group configuration</div>
                  <div v-if="d.MissingMembers.length > 0">
                    <strong>Should be members</strong> ({{ d.MissingMembers.length }}):
                    {{ d.MissingMembers.join(', ') }}
                  </div>
                  <div v-if="d.ExtraMembers.length > 0">
                    <strong>Should not be members</strong> ({{ d.ExtraMembers.length }}):
                    {{ d.ExtraMembers.join(', ') }}
                  </div>
                </div>
              </div>
            </div>
          </div>

          <!-- Rebuild -->
          <div class="flex items-center gap-4 pt-2 border-t border-surface-200">
            <CoarButton variant="secondary" size="s" :loading="rebuilding" @click="rebuildProjections">
              {{ t('admin.settings.rebuildProjections', {}, 'Rebuild Projections') }}
            </CoarButton>
            <p class="text-sm text-surface-500">{{ t('admin.settings.rebuildDescription', {}, 'Rebuilds all read models from the event store. Use if data appears inconsistent.') }}</p>
          </div>
          <p v-if="rebuildResult" class="text-sm" :class="rebuildResult.ok ? 'text-green-600' : 'text-red-600'">
            {{ rebuildResult.message }}
          </p>
        </div>
      </CoarCard>
    </div>
  </div>
</template>
