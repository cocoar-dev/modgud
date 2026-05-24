<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarCard, CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'

const { t, language } = useI18n()
const ui = useUI()
const router = useRouter()
const settingsStore = useRealmSettingsStore()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Plattform')
  ctx.header.subTitle = t('admin.customization.pages.title', {}, 'Pages')
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

interface PageSlot {
  slug: string
  label: string
  description: string
  icon: string
}

// Hard-coded list of the SPA's customisable page-slots. As more pages
// become builder-eligible (register, mfa, etc.) add them here — backend
// stores a dictionary so no API change is needed.
const slots: PageSlot[] = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login'),
    description: t('admin.customization.pages.login.hint', {}, 'Username + password + provider buttons.'),
    icon: 'log-in' },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout'),
    description: t('admin.customization.pages.logout.hint', {}, 'Goodbye screen after sign-out.'),
    icon: 'log-out' },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password'),
    description: t('admin.customization.pages.passwordForgot.hint', {}, 'Email address to receive a reset link.'),
    icon: 'key' },
]

const customisedSlugs = ref<Set<string>>(new Set())

onMounted(async () => {
  try {
    const dto = await settingsStore.load()
    const set = new Set<string>()
    if (dto.Pages) {
      for (const [slug, schema] of Object.entries(dto.Pages)) {
        if (schema && schema.trim().length > 0) set.add(slug)
      }
    }
    customisedSlugs.value = set
  } catch { /* ignore — show all as default */ }
})

const tiles = computed(() => slots.map((s) => ({
  ...s,
  customised: customisedSlugs.value.has(s.slug),
})))

function open(slug: string) {
  router.push(`/plattform/customization/pages/${slug}`)
}
</script>

<template>
  <div class="pages-view">
    <p class="hint">
      {{ t('admin.customization.pages.hint', {}, 'Compose the SPA pages your users land on. Pages without a saved schema fall back to the hardcoded view. Drag elements, configure them, save — the renderer (still WIP) will pick them up at runtime.') }}
    </p>

    <div class="tile-grid">
      <CoarCard
        v-for="tile in tiles"
        :key="tile.slug"
        class="page-tile"
        :class="{ 'page-tile-customised': tile.customised }"
        @click="open(tile.slug)">
        <div class="tile-icon">
          <span class="icon-fallback">{{ tile.label.charAt(0) }}</span>
        </div>
        <div class="tile-meta">
          <div class="tile-label">{{ tile.label }}</div>
          <div class="tile-desc">{{ tile.description }}</div>
          <CoarTag :variant="tile.customised ? 'success' : 'neutral'" class="tile-badge">
            {{ tile.customised
              ? t('admin.customization.pages.customised', {}, 'Customised')
              : t('admin.customization.pages.default', {}, 'Default') }}
          </CoarTag>
        </div>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.pages-view {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem;
  min-height: 0;
  flex: 1;
}

.hint {
  margin: 0;
  font-size: 0.85rem;
  color: var(--coar-text-neutral-secondary);
}

.tile-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 0.75rem;
}

.page-tile {
  display: flex;
  gap: 0.75rem;
  align-items: stretch;
  padding: 0.75rem;
  cursor: pointer;
  transition: transform 80ms ease, box-shadow 80ms ease;
  border: 1px solid var(--coar-border-neutral-secondary);
}

.page-tile:hover {
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
}

.page-tile-customised {
  border-color: var(--coar-text-accent-primary, #4f46e5);
}

.tile-icon {
  width: 56px;
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--coar-background-neutral-primary);
  border-radius: 0.5rem;
  flex-shrink: 0;
}

.icon-fallback {
  font-size: 1.5rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary);
}

.tile-meta {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
  flex: 1;
}

.tile-label {
  font-size: 0.95rem;
  font-weight: 600;
}

.tile-desc {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary);
  flex: 1;
}

.tile-badge {
  align-self: flex-start;
}
</style>
