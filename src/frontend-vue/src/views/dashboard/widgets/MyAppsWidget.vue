<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { CoarTag } from '@cocoar/vue-ui'
import { useHttpClient } from '@/composables/useHttpClient'
import type { WidgetOptions } from '../layout'
import { useRelativeTime } from '../relativeTime'
import WidgetCard from './WidgetCard.vue'

const props = defineProps<{ options: WidgetOptions }>()

const { t } = useI18n()
const relativeTime = useRelativeTime()

// Mirrors MyAppDto (DashboardEndpoints.cs): the clients this account authorized.
interface MyApp {
  Name: string
  AppName?: string | null
  FirstAuthorizedAt?: string | null
  LastAuthorizedAt?: string | null
}

const apps = ref<MyApp[]>([])
const loading = ref(true)
const failed = ref(false)

onMounted(async () => {
  try {
    apps.value = await useHttpClient('/api/dashboard/my-apps').get<MyApp[]>()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

const limit = computed(() => Number(props.options.rows?.[0]) || 5)
const top = computed(() => apps.value.slice(0, limit.value))
const extra = computed(() => Math.max(0, apps.value.length - limit.value))
</script>

<template>
  <WidgetCard
    :title="t('dashboard.myApps.title', {}, 'My apps')"
    icon="app-window"
    :subtitle="t('dashboard.myApps.subtitle', {}, 'Where this account is signed in')"
    :loading="loading"
    :failed="failed"
    :empty="apps.length === 0 ? t('dashboard.myApps.none', {}, 'You have not signed in to any app with this account yet.') : null"
  >
    <div v-for="app in top" :key="app.Name" class="widget-row my-app">
      <span class="widget-row__label">
        <span class="widget-row__title">{{ app.Name }}</span>
        <span v-if="app.AppName && app.AppName !== app.Name" class="widget-row__sub">{{ app.AppName }}</span>
      </span>
      <span v-if="app.LastAuthorizedAt" class="widget-row__meta">
        <CoarTag variant="neutral" size="s">{{ relativeTime(app.LastAuthorizedAt) }}</CoarTag>
      </span>
    </div>
    <div v-if="extra > 0" class="widget-row__more">
      {{ t('dashboard.sessions.more', { n: extra }, '+{n} more') }}
    </div>
  </WidgetCard>
</template>

<style scoped>
/* Informational rows — nothing to open, so no pointer and no hover wash. */
.my-app,
.my-app:hover {
  cursor: default;
  background: none;
}
</style>
