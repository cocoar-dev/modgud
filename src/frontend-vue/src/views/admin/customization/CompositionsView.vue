<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarCard, CoarNotice, CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import type { PageCompositionSummary } from '@cocoar/vue-page-builder'
import { useUI } from '@/composables/useUI'
import { usePageCompositionsApi } from '@/composables/usePageCompositionsApi'

const { t } = useI18n()
const ui = useUI()
const router = useRouter()
const { repository } = usePageCompositionsApi()

const compositions = ref<readonly PageCompositionSummary[]>([])
const loading = ref(true)
const error = ref<string | null>(null)

ui.set(ctx => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('admin.customization.compositions.title', {}, 'Compositions')
  ctx.header.icon = 'copy'
  ctx.content.container = true
})

async function load() {
  loading.value = true
  error.value = null
  try {
    compositions.value = await repository.list()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    loading.value = false
  }
}

function edit(id: string) {
  router.push(`/platform/customization/compositions/${encodeURIComponent(id)}`)
}

onMounted(load)
</script>

<template>
  <div class="compositions-page">
    <div class="page-heading">
      <div>
        <h2>{{ t('admin.customization.compositions.title', {}, 'Compositions') }}</h2>
        <p>{{ t('admin.customization.compositions.hint', {}, 'Build reusable, immutable-versioned subtrees here. Pages only consume pinned versions.') }}</p>
      </div>
      <CoarButton icon-start="plus" @click="router.push('/platform/customization/compositions/new')">
        {{ t('admin.customization.compositions.new', {}, 'New composition') }}
      </CoarButton>
    </div>

    <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    <p v-if="loading" class="muted">{{ t('common.loading', {}, 'Loading…') }}</p>
    <CoarNotice v-else-if="compositions.length === 0" variant="info">
      {{ t('admin.customization.compositions.empty', {}, 'No compositions yet. Create a reusable subtree, then insert it into Login, Logout or another page.') }}
    </CoarNotice>

    <div v-else class="composition-grid">
      <CoarCard v-for="item in compositions" :key="item.id" class="composition-card">
        <div class="composition-card__copy">
          <strong>{{ item.name }}</strong>
          <code>{{ item.id }}</code>
          <div class="versions">
            <CoarTag>v{{ item.latestVersion }}</CoarTag>
            <span>{{ item.versions?.length ?? 1 }} immutable version(s)</span>
          </div>
        </div>
        <CoarButton size="s" variant="secondary" @click="edit(item.id)">
          {{ t('common.edit', {}, 'Edit') }}
        </CoarButton>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.compositions-page { display: grid; gap: 1rem; padding-block: 1rem; }
.page-heading, .composition-card, .versions { display: flex; align-items: center; gap: 1rem; }
.page-heading { justify-content: space-between; }
.page-heading h2, .page-heading p { margin: 0; }
.page-heading p, .muted, .versions { color: var(--coar-text-neutral-secondary); font-size: .875rem; }
.composition-grid { display: grid; gap: .75rem; }
.composition-card { justify-content: space-between; padding: 1rem; }
.composition-card__copy { min-width: 0; display: grid; gap: .35rem; }
.composition-card code { color: var(--coar-text-neutral-tertiary); font-size: .75rem; }
</style>
