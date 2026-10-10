<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { useHttpClient } from '@/composables/useHttpClient'
import type { KpiTile } from '../kpiTile'
import KpiCard from '../KpiCard.vue'

const { t } = useI18n()
const router = useRouter()

const count = ref<number | null>(null)
const failed = ref(false)

onMounted(async () => {
  try {
    count.value = (await useHttpClient('/api/account/passkey').get<unknown[]>()).length
  } catch {
    failed.value = true
  }
})

const tile = computed<KpiTile>(() => ({
  key: 'myPasskeys',
  icon: 'fingerprint',
  tone: count.value ? 'emerald' : 'amber',
  caption: t('dashboard.kpi.myPasskeys', {}, 'My passkeys'),
  loading: count.value === null && !failed.value,
  value: failed.value ? '–' : (count.value === null ? null : String(count.value)),
  // No passkey yet is worth a nudge, not an alarm.
  warn: count.value === 0,
  onClick: () => router.push('/profile'),
}))
</script>

<template>
  <KpiCard :tile="tile" class="h-full" />
</template>
