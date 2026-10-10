<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarTag } from '@cocoar/vue-ui'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import WidgetCard from './WidgetCard.vue'

const { t } = useI18n()
const router = useRouter()
const store = useLoginProviderStore()

const loading = ref(true)
const failed = ref(false)
const providers = computed(() => store.providers)

onMounted(async () => {
  try {
    await store.loadAll()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

// The provider detail is a URL-fragment modal on the list page — the id in the
// hash opens it on arrival.
function open(id: string) {
  router.push({ path: '/admin/login-providers', hash: `#${id}` })
}
</script>

<template>
  <WidgetCard
    :title="t('dashboard.loginProviderStatus.title', {}, 'Login providers')"
    icon="log-in"
    :loading="loading"
    :failed="failed"
    :empty="providers.length === 0 ? t('dashboard.loginProviderStatus.none', {}, 'No providers configured.') : null"
    :cta="t('dashboard.loginProviderStatus.cta', {}, 'Manage →')"
    @open="router.push('/admin/login-providers')"
  >
    <button v-for="p in providers" :key="p.Id" type="button" class="widget-row" @click="open(p.Id)">
      <span class="widget-row__label"><span class="widget-row__title">{{ p.DisplayName }}</span></span>
      <span class="widget-row__meta">
        <CoarTag :variant="p.Enabled ? 'success' : 'neutral'" size="s">
          {{ p.Enabled
            ? t('dashboard.loginProviderStatus.enabled', {}, 'Enabled')
            : t('dashboard.loginProviderStatus.disabled', {}, 'Disabled') }}
        </CoarTag>
        <CoarTag v-if="p.Type === 'Internal'" variant="neutral" size="s">
          {{ t('dashboard.loginProviderStatus.system', {}, 'System') }}
        </CoarTag>
        <CoarTag v-else variant="info" size="s">{{ p.Flavor }}</CoarTag>
      </span>
    </button>
  </WidgetCard>
</template>
