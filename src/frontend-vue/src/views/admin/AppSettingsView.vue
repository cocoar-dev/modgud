<script setup lang="ts">
import { ref, watch } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { CoarCard, CoarButton, CoarPopconfirm } from '@cocoar/vue-ui'

const { t, language } = useI18n()
const projectionHttp = useHttpClient('/api/admin/projections')

useRoutedModals()
const { navigateToModal } = useFragmentNavigation()

const ui = useUI()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('nav.settings', {}, 'Einstellungen')
  ctx.header.icon = 'settings'
  ctx.content.container = false
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

// Consistency check now lives in a routed modal — see
// `ConsistencyCheckModal.vue` and the matching `routedFragments` entry
// on the /platform/settings route. The button just navigates there.
function openConsistencyCheck() {
  navigateToModal('consistency-check')
}
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 overflow-auto p-4">
    <div class="w-full mx-auto space-y-6">
      <CoarCard elevated>
        <div class="p-6 space-y-4">
          <h2 class="text-lg font-semibold">{{ t('admin.settings.maintenance', {}, 'Maintenance') }}</h2>

          <!-- Consistency check — routes into a modal with per-check breakdown. -->
          <div class="flex items-center gap-4">
            <CoarButton variant="secondary" size="s" icon-start="shield-check" @click="openConsistencyCheck">
              {{ t('admin.settings.consistencyCheck', {}, 'Consistency Check') }}
            </CoarButton>
            <p class="text-sm text-surface-500">
              {{ t('admin.settings.consistencyDescription', {},
                'Verifies principal projections, group memberships, auto-group predicates, and cross-references. Opens a detailed report.') }}
            </p>
          </div>

          <!-- Projection rebuild — destructive (replays the whole event store),
               so it sits behind a confirm and uses the danger variant so it
               doesn't read as a benign action (UI/UX wave 4, #15). -->
          <div class="flex items-center gap-4 pt-2 border-t border-surface-200">
            <CoarPopconfirm
              :title="t('admin.settings.rebuildConfirmTitle', {}, 'Rebuild all projections?')"
              :message="t('admin.settings.rebuildConfirmMessage', {}, 'This replays the entire event store and rebuilds every read model. It can take a while and read models may be briefly incomplete. Run only if data appears inconsistent.')"
              @confirmed="rebuildProjections">
              <CoarButton variant="danger" size="s" :loading="rebuilding">
                {{ t('admin.settings.rebuildProjections', {}, 'Rebuild Projections') }}
              </CoarButton>
            </CoarPopconfirm>
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
