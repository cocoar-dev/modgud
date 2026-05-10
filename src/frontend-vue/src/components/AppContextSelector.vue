<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { CoarSelect } from '@cocoar/vue-ui'
import { useApplicationsStore } from '@/stores/applications.store'
import { useAppContextStore } from '@/stores/appContext.store'

/**
 * Workspace-switcher in the admin topbar. Picks the App-scope the
 * Scopes / APIs / Clients / Roles / Groups grids should filter on.
 *
 * <para>Empty-state friendly: even with zero user-defined apps, the
 * dropdown still offers <c>Alle anzeigen</c>, <c>Realm-wide</c>, and
 * the system apps. Default selection on first load is <c>all</c>
 * (unfiltered) — same view as before this control existed.</para>
 */
const appsStore = useApplicationsStore()
const ctx = useAppContextStore()

onMounted(() => appsStore.initialize())

const options = computed(() => {
  const apps = [...appsStore.apps].sort((a, b) =>
    Number(b.IsSystem) - Number(a.IsSystem) || a.DisplayName.localeCompare(b.DisplayName))
  return [
    { value: 'all',    label: 'Alle anzeigen' },
    { value: 'global', label: 'Realm-wide (Global)' },
    ...apps.map((a) => ({
      value: a.Id,
      label: a.IsSystem ? `${a.DisplayName} · System` : a.DisplayName,
    })),
  ]
})

const value = computed({
  get: () => ctx.selection,
  set: (v: string) => ctx.set(v),
})
</script>

<template>
  <div class="app-context-selector" :title="`Filter the OAuth/Authorization grids by workspace`">
    <span class="label">App:</span>
    <CoarSelect v-model="value" :options="options" size="s" />
  </div>
</template>

<style scoped>
.app-context-selector {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0 0.5rem 0 0.75rem;
  color: rgba(255, 255, 255, 0.85);
}
.label {
  font-size: 0.78rem;
  font-weight: 500;
  letter-spacing: 0.02em;
  opacity: 0.8;
}
.app-context-selector :deep(.coar-select) {
  min-width: 12rem;
}
</style>
