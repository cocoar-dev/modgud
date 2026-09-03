<script setup lang="ts">
import { CoarMenu, CoarMenuItem } from '@cocoar/vue-ui'
import type { SubNavGroup, SubNavItem } from './sub-nav-types'

/**
 * Multi-Menu Sub-Nav, gruppiert. Mehrere Menus untereinander, jeweils mit
 * Themen-Heading. Wenn der Inhalt nicht in die verfügbare Höhe passt, scrollt
 * der **gesamte Sub-Nav-Container** — die einzelnen Menus shrinken NICHT,
 * keine Inner-Scrollbars.
 *
 * Use-Case: thematisch gruppierte Bereiche wie Admin mit Autorisierung /
 * OAuth / Anpassung / System. Wenn nur eine flache Liste gebraucht wird,
 * stattdessen <SubNavLayout> verwenden.
 */
const props = defineProps<{
  groups: SubNavGroup[]
}>()

// Items OHNE `to` aber MIT `onClick` rendern als <button> via `@clicked`-Pfad.
function onClicked(item: SubNavItem) {
  if (item.disabled) return
  if (item.onClick) item.onClick()
}

function visibleItems(group: SubNavGroup): SubNavItem[] {
  return group.items.filter((i) => i.visible !== false)
}

function visibleGroups(): SubNavGroup[] {
  return props.groups.filter((g) => visibleItems(g).length > 0)
}
</script>

<template>
  <div class="sub-nav-layout">
    <!-- Sub-Nav: feste Spalte links, der GESAMTE Container scrollt wenn
         Inhalt zu groß. Einzelne Menus shrinken nicht. -->
    <aside class="sub-nav">
      <div class="sub-nav-scroll">
        <section
          v-for="group in visibleGroups()"
          :key="group.title ?? 'untitled'"
          class="sub-nav-group"
        >
          <h3 v-if="group.title" class="sub-nav-group-title">{{ group.title }}</h3>
          <CoarMenu class="sub-nav-group-menu">
            <CoarMenuItem
              v-for="item in visibleItems(group)"
              :key="item.label"
              :icon="item.icon"
              :label="item.label"
              :to="item.to"
              :active="item.active"
              :disabled="item.disabled"
              @clicked="onClicked(item)"
            />
          </CoarMenu>
        </section>
      </div>
    </aside>

    <!-- Content: zentriert auf 11/12 der verfügbaren Breite, damit Tabellen/
         Forms nicht direkt an der Sub-Nav-Kante bzw. am rechten Viewport-Rand
         kleben. -->
    <main class="sub-nav-content">
      <div class="sub-nav-content-inner">
        <slot />
      </div>
    </main>
  </div>
</template>

<style scoped>
.sub-nav-layout {
  display: flex;
  flex: 1;
  min-height: 0;
  min-width: 0;
}

.sub-nav {
  flex-shrink: 0;
  width: 14rem;
  min-height: 0;
  display: flex;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
  --coar-menu-min-width: 0;
}

.sub-nav-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 1rem;
  display: flex;
  flex-direction: column;
}

.sub-nav-group + .sub-nav-group {
  margin-top: 1.25rem;
}

.sub-nav-group-menu {
  display: flex;
  width: 100%;
}

.sub-nav-group-title {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--coar-text-neutral-tertiary, #6b7280);
  padding: 0 0.5rem;
  margin: 0 0 0.5rem 0;
}

/* The content area is a BOUNDED box, not a scroller: grid pages (AG Grid) and
   the editors need a fixed height to size themselves. Consequently every page
   that can outgrow the viewport — a long form, a settings page — must be its own
   scroller: root element `flex-1 min-h-0 overflow-y-auto` (see InboxSettingsView,
   BrandingView). A page without that is silently clipped below the fold. */
.sub-nav-content {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  justify-content: center;
  overflow: hidden;
}

.sub-nav-content-inner {
  width: 91.6667%; /* 11/12 */
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
</style>
