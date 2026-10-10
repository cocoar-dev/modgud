<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import {
  CoarButton, CoarCheckbox, CoarIcon, CoarPopconfirm, CoarSegmentedControl, CoarSpinner, useToast,
} from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useHttpClient } from '@/composables/useHttpClient'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useAuthStore } from '@/stores/auth.store'
import { useDashboardStats } from './dashboardStats'
import {
  resolveLayout, resolveOptions,
  type WidgetDefinition, type WidgetOptionChoice, type WidgetOptionDefinition, type WidgetPlacement,
} from './layout'
import { BUILT_IN_LAYOUT, WIDGETS, WIDGET_CATALOG } from './widgets/registry'

const { t, language } = useI18n()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()
const toast = useToast()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('dashboard.title', {}, 'Dashboard')
  ctx.header.icon = 'layout-dashboard'
  ctx.content.container = true
}), { immediate: true })

// ─── Which widgets, in which order ───────────────────────────────────────
// Three layers, first one present wins: the viewer's own arrangement, the
// realm default an admin set, the layout built into the app. Whatever wins is
// then filtered down to the widgets this viewer may see.
const layoutHttp = useHttpClient('/api/dashboard/layout')
const defaultLayoutHttp = useHttpClient('/api/admin/dashboard/default-layout')

const userLayout = ref<WidgetPlacement[] | null>(null)
const realmDefault = ref<WidgetPlacement[] | null>(null)
const layoutLoading = ref(true)

function canSee(def: WidgetDefinition): boolean {
  return def.requirePermissions.length === 0
    || def.requirePermissions.some(p => authStore.hasPermission(p))
}

const baseLayout = computed(() => realmDefault.value ?? BUILT_IN_LAYOUT)
const activeLayout = computed(() =>
  resolveLayout(userLayout.value ?? baseLayout.value, WIDGET_CATALOG, canSee))

const { load: loadStats } = useDashboardStats()

onMounted(async () => {
  loadStats()
  try {
    // The API leaves a layer out of the response when nobody stored it.
    const stored = await layoutHttp.get<{ User?: WidgetPlacement[] | null; RealmDefault?: WidgetPlacement[] | null }>()
    userLayout.value = stored.User ?? null
    realmDefault.value = stored.RealmDefault ?? null
  } catch {
    // Without the stored layouts the built-in one still gives a working dashboard.
  } finally {
    layoutLoading.value = false
  }
})

// ─── Customising ─────────────────────────────────────────────────────────
const editing = ref(false)
const draft = ref<WidgetPlacement[]>([])
const saving = ref(false)
const canManageDefault = computed(() => authStore.hasPermission('realm-settings:write'))

const shown = computed(() => editing.value ? draft.value : activeLayout.value)
const available = computed(() =>
  WIDGETS.filter(def => canSee(def) && !draft.value.some(p => p.Widget === def.id)))

function title(def: WidgetDefinition): string {
  return t(def.titleKey, {}, def.titleEn)
}
function definition(placement: WidgetPlacement): WidgetDefinition {
  return WIDGET_CATALOG.get(placement.Widget)!
}

/** What the widget component is rendered with: its fixed props plus its resolved options. */
function widgetProps(placement: WidgetPlacement): Record<string, unknown> {
  const def = definition(placement)
  return def.options
    ? { ...def.props, options: resolveOptions(def, placement.Options) }
    : { ...def.props }
}

function startEditing() {
  draft.value = activeLayout.value.map(p => ({ ...p }))
  configuring.value = null
  editing.value = true
}

// ─── A widget's own settings ─────────────────────────────────────────────
/** Id of the widget whose settings panel is open, if any. */
const configuring = ref<string | null>(null)

/** A choice is offered only where it can ever show something for this viewer. */
function availableChoices(option: WidgetOptionDefinition): WidgetOptionChoice[] {
  return option.choices.filter(choice =>
    (!choice.requireFeature || appConfig.config.Features[choice.requireFeature])
    && (!choice.requirePermissions || choice.requirePermissions.some(p => authStore.hasPermission(p))))
}

function chosen(placement: WidgetPlacement, option: WidgetOptionDefinition): string[] {
  return resolveOptions(definition(placement), placement.Options)[option.key] ?? []
}

function setOption(index: number, option: WidgetOptionDefinition, values: string[]) {
  draft.value = draft.value.map((p, i) =>
    i === index ? { ...p, Options: { ...p.Options, [option.key]: values } } : p)
}

function toggleChoice(index: number, option: WidgetOptionDefinition, value: string, on: boolean) {
  const placement = draft.value[index]
  if (!placement) return
  const current = chosen(placement, option)
  // Kept in the catalog's order, so the widget shows tiles in a stable sequence.
  setOption(index, option, option.choices
    .map(c => c.value)
    .filter(v => v === value ? on : current.includes(v)))
}

function move(from: number, to: number) {
  if (to < 0 || to >= draft.value.length || from === to) return
  const next = [...draft.value]
  next.splice(to, 0, ...next.splice(from, 1))
  draft.value = next
}

/** Steps a widget to the next narrower / wider size it supports. */
function resize(index: number, step: -1 | 1) {
  const placement = draft.value[index]
  if (!placement) return
  const sizes = definition(placement).sizes
  const next = sizes[sizes.indexOf(placement.Size) + step]
  if (next) draft.value = draft.value.map((p, i) => i === index ? { ...p, Size: next } : p)
}
function canResize(placement: WidgetPlacement, step: -1 | 1): boolean {
  const sizes = definition(placement).sizes
  return sizes[sizes.indexOf(placement.Size) + step] !== undefined
}

function remove(index: number) {
  draft.value = draft.value.filter((_, i) => i !== index)
}
function add(def: WidgetDefinition) {
  draft.value = [...draft.value, { Widget: def.id, Size: def.defaultSize }]
}

// Drag to reorder: the list reorders as the dragged widget passes over another.
const dragIndex = ref<number | null>(null)
function onDragStart(index: number, event: DragEvent) {
  dragIndex.value = index
  event.dataTransfer?.setData('text/plain', draft.value[index]?.Widget ?? '')
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
}
function onDragOver(index: number) {
  if (dragIndex.value === null || dragIndex.value === index) return
  move(dragIndex.value, index)
  dragIndex.value = index
}

async function run(action: () => Promise<void>, done: string) {
  saving.value = true
  try {
    await action()
    toast.success(done)
  } catch {
    toast.error(t('dashboard.customize.saveFailed', {}, 'The dashboard could not be saved.'))
  } finally {
    saving.value = false
  }
}

function save() {
  return run(async () => {
    await layoutHttp.put({ Widgets: draft.value })
    userLayout.value = draft.value
    editing.value = false
  }, t('dashboard.customize.saved', {}, 'Your dashboard was saved.'))
}

function resetMine() {
  return run(async () => {
    await layoutHttp.delete()
    userLayout.value = null
    editing.value = false
  }, t('dashboard.customize.resetDone', {}, 'Your dashboard follows the default again.'))
}

function saveAsRealmDefault() {
  return run(async () => {
    await defaultLayoutHttp.put({ Widgets: draft.value })
    realmDefault.value = draft.value.map(p => ({ ...p }))
  }, t('dashboard.customize.defaultSaved', {}, 'Saved as the default for this realm.'))
}

function removeRealmDefault() {
  return run(async () => {
    await defaultLayoutHttp.delete()
    realmDefault.value = null
  }, t('dashboard.customize.defaultRemoved', {}, 'The realm default was removed; the built-in layout applies.'))
}
</script>

<template>
  <div class="w-full py-6">
    <div v-if="layoutLoading" class="dash-loading"><CoarSpinner size="m" /></div>

    <template v-else>
      <div v-if="!editing" class="dash-toolbar">
        <CoarButton size="s" variant="ghost" icon-start="sliders-horizontal" @click="startEditing">
          {{ t('dashboard.customize.start', {}, 'Customize') }}
        </CoarButton>
      </div>

      <!-- ─── Edit panel ─── -->
      <section v-else class="dash-edit" :aria-label="t('dashboard.customize.start', {}, 'Customize')">
        <div class="dash-edit__row">
          <p class="dash-edit__hint">
            {{ t('dashboard.customize.hint', {}, 'Drag widgets to reorder them, change their width or remove them. This arrangement is yours alone.') }}
          </p>
          <div class="dash-edit__actions">
            <CoarButton size="s" variant="ghost" :disabled="saving" @click="editing = false">
              {{ t('common.cancel', {}, 'Cancel') }}
            </CoarButton>
            <CoarButton v-if="userLayout" size="s" variant="ghost" :disabled="saving" @click="resetMine">
              {{ t('dashboard.customize.reset', {}, 'Back to default') }}
            </CoarButton>
            <CoarButton size="s" :loading="saving" @click="save">
              {{ t('common.save', {}, 'Save') }}
            </CoarButton>
          </div>
        </div>

        <div class="dash-edit__catalog">
          <span class="dash-edit__label">{{ t('dashboard.customize.add', {}, 'Add:') }}</span>
          <span v-if="available.length === 0" class="dash-edit__none">
            {{ t('dashboard.customize.allPlaced', {}, 'Every available widget is on the dashboard.') }}
          </span>
          <button v-for="def in available" :key="def.id" type="button" class="dash-edit__chip" @click="add(def)">
            <CoarIcon name="plus" size="s" />
            {{ title(def) }}
          </button>
        </div>

        <div v-if="canManageDefault" class="dash-edit__row dash-edit__row--realm">
          <p class="dash-edit__hint">
            {{ realmDefault
              ? t('dashboard.customize.realmHintSet', {}, 'This realm has its own default. Users who never arranged their dashboard see it.')
              : t('dashboard.customize.realmHintUnset', {}, 'This realm uses the built-in default. You can make the arrangement below the default for everyone who never arranged their own.') }}
          </p>
          <div class="dash-edit__actions">
            <CoarPopconfirm
              v-if="realmDefault"
              :title="t('dashboard.customize.removeDefaultTitle', {}, 'Remove the realm default?')"
              :message="t('dashboard.customize.removeDefaultMessage', {}, 'Users without their own arrangement go back to the built-in layout.')"
              @confirmed="removeRealmDefault">
              <CoarButton size="s" variant="ghost" :disabled="saving">
                {{ t('dashboard.customize.removeDefault', {}, 'Remove realm default') }}
              </CoarButton>
            </CoarPopconfirm>
            <CoarPopconfirm
              :title="t('dashboard.customize.saveDefaultTitle', {}, 'Use as the realm default?')"
              :message="t('dashboard.customize.saveDefaultMessage', {}, 'Everyone in this realm who has not arranged their own dashboard gets this arrangement. Each user still only sees the widgets they have permission for.')"
              @confirmed="saveAsRealmDefault">
              <CoarButton size="s" variant="secondary" :disabled="saving">
                {{ t('dashboard.customize.saveDefault', {}, 'Save as realm default') }}
              </CoarButton>
            </CoarPopconfirm>
          </div>
        </div>
      </section>

      <p v-if="shown.length === 0" class="dash-empty">
        {{ t('dashboard.customize.empty', {}, 'No widgets on the dashboard.') }}
      </p>

      <div class="dash-grid" :class="{ 'dash-grid--editing': editing }">
        <div
          v-for="(placement, index) in shown"
          :key="placement.Widget"
          class="dash-cell"
          :class="[`dash-cell--${placement.Size}`, { 'dash-cell--dragging': editing && dragIndex === index }]"
          :draggable="editing"
          @dragstart="onDragStart(index, $event)"
          @dragover.prevent="onDragOver(index)"
          @drop.prevent
          @dragend="dragIndex = null"
        >
          <div v-if="editing" class="dash-cell__bar">
            <CoarIcon name="grip-vertical" size="s" class="dash-cell__grip" />
            <span class="dash-cell__name">{{ title(definition(placement)) }}</span>
            <CoarButton
              size="s" variant="ghost" icon-start="arrow-left" :disabled="index === 0"
              :title="t('dashboard.customize.moveEarlier', {}, 'Move earlier')" @click="move(index, index - 1)" />
            <CoarButton
              size="s" variant="ghost" icon-start="arrow-right" :disabled="index === shown.length - 1"
              :title="t('dashboard.customize.moveLater', {}, 'Move later')" @click="move(index, index + 1)" />
            <CoarButton
              size="s" variant="ghost" icon-start="minus" :disabled="!canResize(placement, -1)"
              :title="t('dashboard.customize.narrower', {}, 'Narrower')" @click="resize(index, -1)" />
            <CoarButton
              size="s" variant="ghost" icon-start="plus" :disabled="!canResize(placement, 1)"
              :title="t('dashboard.customize.wider', {}, 'Wider')" @click="resize(index, 1)" />
            <CoarButton
              v-if="definition(placement).options"
              size="s" :variant="configuring === placement.Widget ? 'secondary' : 'ghost'" icon-start="settings"
              :title="t('dashboard.customize.settings', {}, 'Settings')"
              @click="configuring = configuring === placement.Widget ? null : placement.Widget" />
            <CoarButton
              size="s" variant="ghost" icon-start="x"
              :title="t('dashboard.customize.remove', {}, 'Remove')" @click="remove(index)" />
          </div>

          <div v-if="editing && configuring === placement.Widget" class="dash-cell__settings">
            <div v-for="option in definition(placement).options" :key="option.key" class="dash-cell__option">
              <span class="dash-cell__option-label">{{ t(option.labelKey, {}, option.labelEn) }}</span>
              <div v-if="option.kind === 'multi'" class="dash-cell__choices">
                <CoarCheckbox
                  v-for="choice in availableChoices(option)" :key="choice.value"
                  :label="t(choice.labelKey, {}, choice.labelEn)"
                  :model-value="chosen(placement, option).includes(choice.value)"
                  @update:model-value="toggleChoice(index, option, choice.value, $event)" />
              </div>
              <CoarSegmentedControl
                v-else
                :options="availableChoices(option).map(c => ({ value: c.value, label: t(c.labelKey, {}, c.labelEn) }))"
                :model-value="chosen(placement, option)[0] ?? ''"
                @update:model-value="setOption(index, option, [$event])" />
            </div>
          </div>
          <!-- While arranging, the widget is a preview: not clickable, not focusable. -->
          <div class="dash-cell__content" :inert="editing || undefined">
            <component :is="definition(placement).component" v-bind="widgetProps(placement)" />
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.dash-loading {
  display: flex;
  justify-content: center;
  padding: 3rem 0;
}
.dash-toolbar {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 0.5rem;
}
.dash-empty {
  padding: 2rem 0;
  text-align: center;
  color: var(--coar-text-neutral-secondary, #6b7280);
}

/* ── Grid: 12 columns; a size is a column span. Narrower viewports fold the
      spans down so nothing gets thinner than it can lay out. ── */
.dash-grid {
  display: grid;
  grid-template-columns: repeat(12, minmax(0, 1fr));
  gap: 1rem;
  /* Breathing room below the last row when scrolled to the end. */
  padding-bottom: 2rem;
}
.dash-cell {
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.dash-cell__content {
  flex: 1;
  min-height: 0;
}
.dash-cell--xs { grid-column: span 2; }
.dash-cell--s { grid-column: span 3; }
.dash-cell--m { grid-column: span 4; }
.dash-cell--l { grid-column: span 6; }
.dash-cell--xl { grid-column: span 8; }
.dash-cell--full { grid-column: span 12; }

@media (max-width: 1279px) {
  .dash-cell--xs { grid-column: span 3; }
  .dash-cell--s { grid-column: span 3; }
  .dash-cell--m { grid-column: span 6; }
  .dash-cell--l { grid-column: span 6; }
  .dash-cell--xl { grid-column: span 12; }
}
@media (max-width: 767px) {
  .dash-cell--xs,
  .dash-cell--s { grid-column: span 6; }
  .dash-cell--m,
  .dash-cell--l { grid-column: span 12; }
}

/* ── Edit mode ── */
.dash-edit {
  margin-bottom: 1rem;
  padding: 0.75rem 1rem;
  border: 1px dashed var(--coar-border-neutral-secondary, #cbd5e1);
  border-radius: 0.5rem;
  background: var(--coar-background-neutral-primary, #ffffff);
}
.dash-edit__row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem 1rem;
}
.dash-edit__row--realm {
  margin-top: 0.75rem;
  padding-top: 0.75rem;
  border-top: 1px solid var(--coar-border-neutral-tertiary, rgba(0, 0, 0, 0.08));
}
.dash-edit__hint {
  flex: 1 1 18rem;
  margin: 0;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.dash-edit__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}
.dash-edit__catalog {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.375rem;
  margin-top: 0.75rem;
  font-size: 0.8125rem;
}
.dash-edit__label,
.dash-edit__none {
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.dash-edit__chip {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.625rem 0.25rem 0.375rem;
  border: 1px solid var(--coar-border-neutral-secondary, #cbd5e1);
  border-radius: 9999px;
  background: transparent;
  color: inherit;
  font-size: 0.8125rem;
  cursor: pointer;
}
.dash-edit__chip:hover,
.dash-edit__chip:focus-visible {
  border-color: var(--coar-accent, #1077be);
}

.dash-grid--editing .dash-cell {
  border: 1px dashed var(--coar-border-neutral-secondary, #cbd5e1);
  border-radius: 0.625rem;
  padding: 0.25rem;
  cursor: grab;
}
.dash-cell--dragging {
  opacity: 0.4;
}
.dash-cell__bar {
  display: flex;
  align-items: center;
  gap: 0.125rem;
  padding: 0 0.125rem 0.25rem 0.25rem;
}
.dash-cell__settings {
  display: flex;
  flex-direction: column;
  gap: 0.625rem;
  margin: 0 0.25rem 0.375rem;
  padding: 0.625rem 0.75rem;
  border-radius: 0.375rem;
  background: var(--coar-background-neutral-primary, #ffffff);
  font-size: 0.8125rem;
  cursor: default;
}
.dash-cell__option {
  display: flex;
  flex-direction: column;
  gap: 0.375rem;
}
.dash-cell__option-label {
  font-weight: 600;
}
.dash-cell__choices {
  display: flex;
  flex-wrap: wrap;
  gap: 0.375rem 1rem;
}
.dash-cell__grip {
  color: var(--coar-text-neutral-secondary, #9ca3af);
}
.dash-cell__name {
  flex: 1;
  min-width: 0;
  margin-left: 0.25rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.8125rem;
  font-weight: 600;
}
</style>
