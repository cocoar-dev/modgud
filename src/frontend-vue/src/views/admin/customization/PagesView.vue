<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarNote, CoarSelect, CoarTag, useDialog } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useRealmPagesApi, type RealmSlotDto } from '@/composables/usePagesApi'

const { t, language } = useI18n()
const ui = useUI()
const router = useRouter()
const dialog = useDialog()
const api = useRealmPagesApi()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('admin.customization.pages.title', {}, 'Pages')
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

interface PageSlotMeta { slug: string; label: string; description: string }

const slotMeta: PageSlotMeta[] = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login'),
    description: t('admin.customization.pages.login.hint', {}, 'Username + password + provider buttons.') },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout'),
    description: t('admin.customization.pages.logout.hint', {}, 'Goodbye screen after sign-out.') },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password'),
    description: t('admin.customization.pages.passwordForgot.hint', {}, 'Email address to receive a reset link.') },
]

const slots = reactive<Record<string, RealmSlotDto>>({})
const error = ref<string | null>(null)
const busy = ref(false)

const BUILT_IN = '__builtin__'

function slotOf(slug: string): RealmSlotDto {
  return slots[slug] ?? { Slug: slug, ActiveVariantId: null, Variants: [] }
}

async function reload() {
  try {
    const { Slots } = await api.listSlots()
    const bySlug = new Map(Slots.map((s) => [s.Slug, s]))
    for (const m of slotMeta) {
      slots[m.slug] = bySlug.get(m.slug) ?? { Slug: m.slug, ActiveVariantId: null, Variants: [] }
    }
  } catch (e: any) { error.value = e?.message ?? String(e) }
}

onMounted(reload)

function activeOptions(slot: RealmSlotDto) {
  return [
    { value: BUILT_IN, label: t('admin.customization.pages.builtin', {}, 'Built-in (default)') },
    ...slot.Variants.map((v) => ({ value: v.Id, label: v.Name })),
  ]
}

async function setActive(slug: string, value: string | null) {
  busy.value = true
  error.value = null
  try {
    await api.setActive(slug, (value === null || value === BUILT_IN) ? null : value)
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) } finally { busy.value = false }
}

function newVariant(slug: string) {
  router.push(`/platform/customization/pages/${slug}/new`)
}

function editVariant(slug: string, id: string) {
  router.push(`/platform/customization/pages/${slug}/${id}`)
}

async function removeVariant(slot: RealmSlotDto, id: string, name: string) {
  const confirmed = await dialog.confirm({
    title: t('admin.customization.pages.deleteTitle', {}, 'Delete variant'),
    message: t('admin.customization.pages.deleteMessage', { name },
      `Delete "${name}"? If it is the active page, the slot reverts to the built-in view.`),
    confirmText: t('common.delete', {}, 'Delete'),
    confirmVariant: 'danger',
  }).result
  if (!confirmed) return
  busy.value = true
  try {
    await api.deleteVariant(slot.Slug, id)
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) } finally { busy.value = false }
}
</script>

<template>
  <div class="pages-view">
    <p class="hint">
      {{ t('admin.customization.pages.hintV2', {}, 'Author one or more variants per page, then choose which is live for this realm. Slots set to "Built-in" render the fixed default. Applications can override each slot in their own settings.') }}
    </p>

    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>

    <div v-for="m in slotMeta" :key="m.slug" class="slot">
      <div class="slot-head">
        <div class="slot-meta">
          <div class="slot-label">{{ m.label }}</div>
          <div class="slot-desc">{{ m.description }}</div>
        </div>
        <div class="slot-active">
          <label class="active-label">{{ t('admin.customization.pages.activeForRealm', {}, 'Active for realm') }}</label>
          <CoarSelect
            :model-value="slotOf(m.slug).ActiveVariantId ?? BUILT_IN"
            :options="activeOptions(slotOf(m.slug))"
            :disabled="busy"
            size="s"
            @update:model-value="(v: string | null) => setActive(m.slug, v)" />
        </div>
      </div>

      <div class="variants">
        <div
          v-for="v in slotOf(m.slug).Variants"
          :key="v.Id"
          class="variant"
          :class="{ 'variant-active': v.Id === slotOf(m.slug).ActiveVariantId }">
          <div class="variant-meta">
            <span class="variant-name">{{ v.Name }}</span>
            <CoarTag v-if="v.Id === slotOf(m.slug).ActiveVariantId" variant="success" class="variant-badge">
              {{ t('admin.customization.pages.liveRealm', {}, 'Live · realm') }}
            </CoarTag>
            <CoarTag v-else variant="neutral" class="variant-badge">
              {{ t('admin.customization.pages.unused', {}, 'Not active') }}
            </CoarTag>
          </div>
          <div class="variant-actions">
            <CoarButton size="s" variant="secondary" icon-start="pencil" @click="editVariant(m.slug, v.Id)">
              {{ t('common.edit', {}, 'Edit') }}
            </CoarButton>
            <CoarButton size="s" variant="ghost" icon-start="trash-2" :disabled="busy" @click="removeVariant(slotOf(m.slug), v.Id, v.Name)">
              {{ t('common.delete', {}, 'Delete') }}
            </CoarButton>
          </div>
        </div>

        <CoarButton size="s" variant="ghost" icon-start="plus" class="add-variant" @click="newVariant(m.slug)">
          {{ t('admin.customization.pages.newVariant', {}, 'New variant') }}
        </CoarButton>
      </div>
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

.slot {
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 0.5rem;
  padding: 0.75rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.slot-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
}

.slot-label { font-size: 0.95rem; font-weight: 600; }
.slot-desc { font-size: 0.78rem; color: var(--coar-text-neutral-secondary); }

.slot-active { display: flex; flex-direction: column; gap: 0.15rem; min-width: 220px; }
.active-label { font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.03em; color: var(--coar-text-neutral-secondary); }

.variants { display: flex; flex-direction: column; gap: 0.4rem; }

.variant {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.5rem 0.6rem;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 0.4rem;
  background: var(--coar-background-neutral-primary);
}

.variant-active { border-color: var(--coar-text-accent-primary, #4f46e5); }

.variant-meta { display: flex; align-items: center; gap: 0.5rem; min-width: 0; }
.variant-name { font-size: 0.88rem; font-weight: 500; }
.variant-badge { flex-shrink: 0; }
.variant-actions { display: flex; gap: 0.25rem; flex-shrink: 0; }

.add-variant { align-self: flex-start; }
</style>
